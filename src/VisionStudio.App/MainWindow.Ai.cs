using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Emgu.CV;
using VisionStudio.Ai;
using VisionStudio.Ai.Abstractions;
using VisionStudio.CodeGen;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.App;

public sealed partial class MainWindow : Window {
  private async void AiActivity_Click(object? sender, RoutedEventArgs e) {
    SwitchActivity(WorkbenchActivity.Ai);
    await LoadAiModelsAsync();
  }

  private void RefreshAiImageReferences() {
    AiImageReferencesPanel.Children.Clear();
    foreach (var reference in AiImageReferenceCatalog.Create(_workflow)) {
      var button = new Button {
        Content = reference.PromptToken,
        Tag = reference,
        Margin = new Thickness(0, 0, 5, 5),
        Padding = new Thickness(7, 3),
        FontSize = 10
      };
      ToolTip.SetTip(button, _localization.IsChinese ? "点击插入图像引用；也可以从画布节点右上角编号拖入" : "Click to insert; you can also drag the badge from the canvas node");
      button.Click += (_, _) => InsertAiImageReference(reference);
      AiImageReferencesPanel.Children.Add(button);
    }
    AiImageReferencesPanel.IsVisible = AiImageReferencesPanel.Children.Count > 0;
  }

  private void InsertAiImageReference(AiImageReference reference) {
    string token = reference.PromptToken + " ";
    int position = Math.Clamp(AiPromptTextBox.CaretIndex, 0, AiPromptTextBox.Text?.Length ?? 0);
    AiPromptTextBox.Text = (AiPromptTextBox.Text ?? "").Insert(position, token);
    AiPromptTextBox.CaretIndex = position + token.Length;
    AiPromptTextBox.Focus();
  }

  private void AiPrompt_DragOver(object? sender, DragEventArgs e) {
    e.DragEffects = string.IsNullOrWhiteSpace(e.DataTransfer.TryGetText()) ? DragDropEffects.None : DragDropEffects.Copy;
    e.Handled = true;
  }

  private void AiPrompt_Drop(object? sender, DragEventArgs e) {
    string? text = e.DataTransfer.TryGetText();
    if (string.IsNullOrWhiteSpace(text)) {
      return;
    }

    var reference = AiImageReferenceCatalog.Create(_workflow).FirstOrDefault(item =>
        text.Contains(item.Reference, StringComparison.OrdinalIgnoreCase));
    if (reference is not null) {
      InsertAiImageReference(reference);
    }

    e.Handled = true;
  }

  private async Task LoadAiModelsAsync() {
    try {
      string? selectedId = (AiModelComboBox.SelectedItem as AiModelProfile)?.Id ?? _activeAiProfileId;
      _aiProfiles.Clear();
      _aiProfiles.AddRange(await _aiModelStore.LoadAsync());
      string? testEndpoint = Environment.GetEnvironmentVariable("VISIONSTUDIO_AI_TEST_ENDPOINT");
      if (Environment.GetEnvironmentVariable("VISIONSTUDIO_TEST_MODE") == "1" && !string.IsNullOrWhiteSpace(testEndpoint)) {
        _aiProfiles.Add(new("__ui_test__", "UI Test OpenAI", testEndpoint, "ui-test", null, false, 2048));
      }

      AiModelComboBox.ItemsSource = null;
      AiModelComboBox.ItemsSource = _aiProfiles;
      var contexts = await _aiContextStore.LoadAsync();
      _aiContexts.Clear();
      foreach (var pair in contexts) {
        _aiContexts[pair.Key] = [.. pair.Value.Select(MigrateLegacyTurn)];
      }

      AiModelComboBox.SelectedItem = _aiProfiles.FirstOrDefault(profile => profile.Id == selectedId) ?? _aiProfiles.FirstOrDefault();
      if (_aiProfiles.Count == 0) {
        AiContextStatusText.Text = _localization.Text("Ai.NoModels");
        AiConversationPanel.Children.Clear();
        AddAiMessage("assistant", _localization.Text("Ai.ConfigureFirst"));
      }
    } catch (Exception exception) {
      AiContextStatusText.Text = UserError(exception);
    }
  }

  private async void AiModelComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
    if (AiModelComboBox.SelectedItem is not AiModelProfile profile) {
      return;
    }

    if (_activeAiProfileId != profile.Id) {
      await DisposeAiProviderAsync();
    }

    _activeAiProfileId = profile.Id;
    _pendingAiPlan = null;
    AiApplyButton.IsEnabled = false;
    RenderAiContext(profile);
  }

  private void RenderAiContext(AiModelProfile profile) {
    AiConversationPanel.Children.Clear();
    foreach (var message in GetAiContext(profile.Id)) {
      switch (message.Kind) {
        case AiChatMessageKinds.Error:
          AddAiError(message.Content);
          break;
        case AiChatMessageKinds.Execution:
          AddAiExecution(message.Content);
          break;
        case AiChatMessageKinds.Sweep:
          AddAiSweep(message.Content);
          break;
        default:
          AddAiMessage(message.Role, message.Content);
          break;
      }
    }

    string kind = _localization.Text("Ai.RemoteModel");
    AiContextStatusText.Text = _localization.Format("Ai.ContextStatus", kind, GetAiContext(profile.Id).Count);
  }

  /// <summary>
  /// Older builds stored error notices as ordinary assistant turns; reclassify them so they are
  /// no longer replayed to the model and get a retry button.
  /// </summary>
  private AiChatMessage MigrateLegacyTurn(AiChatMessage message) {
    if (string.Equals(message.Kind, AiChatMessageKinds.Chat, StringComparison.Ordinal)
        && message.Role == "assistant"
        && (message.Content.StartsWith("AI 错误", StringComparison.Ordinal) || message.Content.StartsWith("AI error", StringComparison.OrdinalIgnoreCase))) {
      return message with { Kind = AiChatMessageKinds.Error };
    }

    return message;
  }

  private string AiContextKey(string modelId) {
    return $"{modelId}::{_project?.Project.Id.ToString("N") ?? "global"}";
  }

  private IReadOnlyList<AiChatMessage> GetAiContext(string modelId) {
    return _aiContexts.TryGetValue(AiContextKey(modelId), out var messages) ? messages : [];
  }

  private Border? _aiLiveCard;
  private TextBlock? _aiLiveReasoning;
  private TextBlock? _aiLiveContent;
  private TextBlock? _aiLiveStatus;
  private readonly System.Text.StringBuilder _aiReasoning = new();
  private readonly System.Text.StringBuilder _aiAnswer = new();
  private DateTime _aiStarted;

  /// <summary>Shows the model's reasoning and answer as they stream in, so waiting is legible.</summary>
  private void BeginAiStream() {
    EndAiStream();
    _aiReasoning.Clear();
    _aiAnswer.Clear();
    _aiStarted = DateTime.UtcNow;
    _aiLiveStatus = new TextBlock { FontSize = 10, Opacity = .7, Text = _localization.Text("Ai.Thinking") };
    _aiLiveReasoning = new TextBlock {
      FontSize = 11,
      Opacity = .62,
      TextWrapping = Avalonia.Media.TextWrapping.Wrap,
      IsVisible = false,
      MaxHeight = 120,
      TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
    };
    _aiLiveContent = new TextBlock {
      FontSize = 12,
      TextWrapping = Avalonia.Media.TextWrapping.Wrap,
      IsVisible = false
    };
    var card = new Border {
      Child = new StackPanel { Spacing = 6, Children = { _aiLiveStatus, _aiLiveReasoning, _aiLiveContent } }
    };
    card.Classes.Add("planCard");
    _aiLiveCard = card;
    AiConversationPanel.Children.Add(card);
    ScrollAiToEnd();
  }

  private void OnAiStreamDelta(AiStreamDelta delta) {
    if (delta.IsReasoning) {
      _aiReasoning.Append(delta.Text);
      if (_aiLiveReasoning is not null) {
        _aiLiveReasoning.Text = Tail(_aiReasoning.ToString(), 900);
        _aiLiveReasoning.IsVisible = true;
      }
    } else {
      _aiAnswer.Append(delta.Text);
      if (_aiLiveContent is not null) {
        _aiLiveContent.Text = _aiAnswer.ToString();
        _aiLiveContent.IsVisible = true;
      }
    }

    if (_aiLiveStatus is not null) {
      _aiLiveStatus.Text = _localization.Format("Ai.Streaming", ElapsedAi);
    }

    ScrollAiToEnd();
  }

  private void EndAiStream() {
    if (_aiLiveCard is not null) {
      AiConversationPanel.Children.Remove(_aiLiveCard);
    }

    _aiLiveCard = null;
    _aiLiveReasoning = null;
    _aiLiveContent = null;
    _aiLiveStatus = null;
  }

  private double ElapsedAi => (DateTime.UtcNow - _aiStarted).TotalSeconds;

  private static string Tail(string text, int maximum) {
    return text.Length <= maximum ? text : "…" + text[^maximum..];
  }

  private void ScrollAiToEnd() {
    Avalonia.Threading.Dispatcher.UIThread.Post(() => AiConversationScroll.ScrollToEnd());
  }

  /// <summary>Error turn with a one-click retry that does not stack a duplicate question.</summary>
  private void AddAiError(string content) {
    var retry = new Button {
      Content = _localization.Text("Ai.Retry"),
      FontSize = 10,
      Padding = new Thickness(8, 2),
      HorizontalAlignment = HorizontalAlignment.Left
    };
    retry.Click += AiRetry_Click;
    var card = new Border {
      Child = new StackPanel {
        Spacing = 6,
        Children = {
          new TextBlock { Text = content, FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
          retry
        }
      }
    };
    card.Classes.Add("planCard");
    AiConversationPanel.Children.Add(card);
  }

  /// <summary>Node execution output, shown muted because the model did not say it.</summary>
  private void AddAiExecution(string content) {
    AiConversationPanel.Children.Add(new TextBlock {
      Text = content,
      FontSize = 11,
      Opacity = .65,
      FontStyle = Avalonia.Media.FontStyle.Italic,
      TextWrapping = Avalonia.Media.TextWrapping.Wrap,
      Margin = new Thickness(4, 1)
    });
  }

  /// <summary>Renders a stored sweep as a small line chart.</summary>
  private void AddAiSweep(string json) {
    try {
      var series = JsonSerializer.Deserialize<AiSweepSeries>(json);
      if (series is null || series.Points.Count == 0) {
        return;
      }

      var card = new Border {
        Child = new StackPanel {
          Spacing = 4,
          Children = {
            new TextBlock {
              Text = $"{series.NodeId}.{series.Parameter} → {series.TargetNodeId}.{series.MetricName}",
              FontSize = 10,
              Opacity = .72
            },
            new SweepChartControl(series)
          }
        }
      };
      card.Classes.Add("planCard");
      AutomationProperties.SetName(card, $"{series.NodeId}.{series.Parameter} sweep chart");
      AiConversationPanel.Children.Add(card);
    } catch (JsonException) {
      // A malformed stored series must not break the conversation.
    }
  }

  private void AddAiMessage(string role, string content) {
    var message = new ChatMessageControl(
        role, content,
        _localization.Text("Ai.You"), _localization.Text("Ai.Assistant"),
        _localization.Text("Ai.Quote"), _localization.Text("Ai.CopyCode"));
    message.QuoteRequested += (_, request) => InsertAiQuote(request.Text);
    AiConversationPanel.Children.Add(message);
  }

  /// <summary>
  /// Renders an assistant proposal as a review card. Nothing is written to the canvas until the
  /// user presses the card button (or the toolbar Apply button).
  /// </summary>
  private void AddAiPlanCard(AiWorkflowPlan plan) {
    int nodeActions = plan.Actions.Count(action => action.Type is "add_node" or "add_pipeline" or "remove_node");
    int connections = plan.Actions.Count(action => action.Type == "connect_nodes");
    var apply = new Button {
      Content = _localization.Text("Ai.PlanApply"),
      FontSize = 11,
      Padding = new Thickness(9, 3),
      HorizontalAlignment = HorizontalAlignment.Left
    };
    apply.Click += AiApply_Click;
    var body = new StackPanel {
      Spacing = 5,
      Children = {
        new TextBlock {
          Text = _localization.Text("Ai.PlanTitle"),
          FontSize = 11,
          FontWeight = Avalonia.Media.FontWeight.SemiBold
        },
        new TextBlock {
          Text = plan.Summary,
          FontSize = 12,
          TextWrapping = Avalonia.Media.TextWrapping.Wrap
        },
        new TextBlock {
          Text = _localization.Format("Ai.PlanCounts", nodeActions, connections),
          FontSize = 10,
          Opacity = .65
        },
        apply
      }
    };
    var card = new Border { Child = body };
    card.Classes.Add("planCard");
    AutomationProperties.SetName(card, _localization.Text("Ai.PlanTitle") + ": " + plan.Summary);
    AiConversationPanel.Children.Add(card);
  }

  private void InsertAiQuote(string text) {
    string quoted = string.Join(Environment.NewLine, text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => $"> {line}"));
    string insertion = quoted + Environment.NewLine + Environment.NewLine;
    string current = AiPromptTextBox.Text ?? "";
    int position = Math.Clamp(AiPromptTextBox.CaretIndex, 0, current.Length);
    AiPromptTextBox.Text = current.Insert(position, insertion);
    AiPromptTextBox.CaretIndex = position + insertion.Length;
    AiPromptTextBox.Focus();
  }

  private string? _lastPrompt;
  private string? _lastImageNodeId;

  private async void AiSend_Click(object? sender, RoutedEventArgs e) {
    if (_automation is null || _project is null) { Status(_localization.Text("Status.ProjectRequired")); return; }
    if (AiModelComboBox.SelectedItem is not AiModelProfile profile) { AiContextStatusText.Text = _localization.Text("Ai.NoModels"); return; }
    string? text = AiPromptTextBox.Text?.Trim();
    if (string.IsNullOrWhiteSpace(text)) {
      return;
    }

    _lastPrompt = text;
    _lastImageNodeId = null;
    AiPromptTextBox.Text = "";
    await RunAiTurnAsync(profile, text, appendUser: true);
  }

  /// <summary>Re-runs the last question after a failure without stacking a duplicate user turn.</summary>
  private async void AiRetry_Click(object? sender, RoutedEventArgs e) {
    if (_lastPrompt is null || AiModelComboBox.SelectedItem is not AiModelProfile profile || _aiBusy) {
      return;
    }

    var context = GetAiContext(profile.Id).ToList();
    while (context.Count > 0 && string.Equals(context[^1].Kind, AiChatMessageKinds.Error, StringComparison.Ordinal)) {
      context.RemoveAt(context.Count - 1);
    }

    _aiContexts[AiContextKey(profile.Id)] = context;
    await RunAiTurnAsync(profile, _lastPrompt, appendUser: false);
  }

  private bool _aiBusy;
  private CancellationTokenSource? _aiCancellation;
  private AiSweepSeries? _pendingSweep;

  private void AiStop_Click(object? sender, RoutedEventArgs e) {
    _aiCancellation?.Cancel();
    if (_aiLiveStatus is not null) {
      _aiLiveStatus.Text = _localization.Text("Ai.Stopping");
    }
  }

  private async Task RunAiTurnAsync(AiModelProfile profile, string text, bool appendUser) {
    var cancellation = _aiCancellation = new CancellationTokenSource();
    SetAiBusy(true);
    _pendingAiPlan = null;
    AiApplyButton.IsEnabled = false;
    if (appendUser) {
      _aiContexts[AiContextKey(profile.Id)] = [.. GetAiContext(profile.Id)
          .Concat([new AiChatMessage("user", text, AiChatMessageKinds.Chat)]).TakeLast(40)];
    }

    await _aiContextStore.SaveAsync(_aiContexts);
    RenderAiContext(profile);
    try {
      await EnsureAiProviderAsync(profile);
      BeginAiStream();
      _aiTimer.Start();
      var snapshot = await _automation!.GetSnapshotAsync();
      _pendingAiRevision = snapshot.Revision;
      var imageReferences = AiImageReferenceCatalog.Create(snapshot.Workflow);
      var imageReference = AiImageReferenceCatalog.Resolve(snapshot.Workflow, text, _lastImageNodeId ?? Canvas.SelectedNode?.Id);
      if (imageReferences.Count > 1 && imageReference is null) {
        throw new InvalidOperationException(_localization.IsChinese
            ? "流程中有多张输入图片，请在问题中写明 IMG 编号或名称，或把画布节点右上角的编号拖入对话框。"
            : "This workflow has multiple input images. Mention an IMG number/name or drag the badge from the canvas node into the prompt.");
      }

      _lastImageNodeId = imageReference?.NodeId;
      string requestText = imageReference is null ? text : $"指定图像节点：{imageReference.PromptToken}（nodeId={imageReference.NodeId}）\n{text}";
      // Chat turns only: error notices and execution summaries are UI state, not dialogue.
      var history = GetAiContext(profile.Id)
          .Where(message => string.Equals(message.Kind, AiChatMessageKinds.Chat, StringComparison.Ordinal))
          .ToList();
      var streamProgress = new Progress<AiStreamDelta>(OnAiStreamDelta);
      var toolProgress = new Progress<string>(nodeId => {
        // A tool call starts a new model round; the previous round's text no longer applies.
        _aiAnswer.Clear();
        _aiReasoning.Clear();
        if (_aiLiveContent is not null) {
          _aiLiveContent.Text = "";
          _aiLiveContent.IsVisible = false;
        }

        if (_aiLiveReasoning is not null) {
          _aiLiveReasoning.Text = "";
          _aiLiveReasoning.IsVisible = false;
        }

        if (_aiLiveStatus is null) {
          return;
        }

        string label = nodeId.StartsWith("sweep:", StringComparison.Ordinal)
            ? _localization.Format("Ai.Sweeping", nodeId["sweep:".Length..], ElapsedAi)
            : _localization.Format("Ai.RunningNode", _workflow.Nodes.FirstOrDefault(node => node.Id == nodeId)?.Name ?? nodeId, ElapsedAi);
        _aiLiveStatus.Text = label;
      });
      var response = await _aiAgent!.ProcessAsync(new(
          requestText, imageReference?.NodeId ?? Canvas.SelectedNode?.Id,
          FindAiPreviewImage(imageReference), history), streamProgress, toolProgress, cancellation.Token);
      var turns = GetAiContext(profile.Id)
          .Concat([new AiChatMessage("assistant", response.Message, AiChatMessageKinds.Chat)]).ToList();
      if (_pendingSweep is { } sweep) {
        turns.Add(new AiChatMessage("assistant", JsonSerializer.Serialize(sweep), AiChatMessageKinds.Sweep));
        _pendingSweep = null;
      }

      _aiContexts[AiContextKey(profile.Id)] = [.. turns.TakeLast(40)];
      await _aiContextStore.SaveAsync(_aiContexts);
      _aiTimer.Stop();
      EndAiStream();
      _pendingAiPlan = response.Plan;
      RenderAiContext(profile);
      if (response.Plan is not null) {
        AddAiPlanCard(response.Plan);
      }

      AiApplyButton.IsEnabled = response.Plan is not null;
    } catch (OperationCanceledException) {
      _aiContexts[AiContextKey(profile.Id)] = [.. GetAiContext(profile.Id)
          .Concat([new AiChatMessage("assistant", _localization.Text("Ai.Stopped"), AiChatMessageKinds.Error)]).TakeLast(40)];
      await _aiContextStore.SaveAsync(_aiContexts);
      RenderAiContext(profile);
    } catch (Exception exception) {
      string error = _localization.Format("Ai.Error", UserError(exception));
      _aiContexts[AiContextKey(profile.Id)] = [.. GetAiContext(profile.Id)
          .Concat([new AiChatMessage("assistant", error, AiChatMessageKinds.Error)]).TakeLast(40)];
      await _aiContextStore.SaveAsync(_aiContexts);
      RenderAiContext(profile);
    } finally {
      _aiTimer.Stop();
      EndAiStream();
      if (ReferenceEquals(_aiCancellation, cancellation)) {
        _aiCancellation = null;
      }

      cancellation.Dispose();
      SetAiBusy(false);
    }
  }

  private readonly Avalonia.Threading.DispatcherTimer _aiTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

  private void OnAiTick(object? sender, EventArgs e) {
    if (_aiLiveStatus is not null) {
      _aiLiveStatus.Text = _localization.Format("Ai.Streaming", ElapsedAi);
    }
  }

  private async void AiApply_Click(object? sender, RoutedEventArgs e) {
    if (_pendingAiPlan is null || _aiAgent is null || AiModelComboBox.SelectedItem is not AiModelProfile profile) {
      return;
    }

    SetAiBusy(true);
    try {
      var appliedPlan = _pendingAiPlan;
      var response = await _aiAgent.ApplyPlanAsync(_pendingAiPlan, _pendingAiRevision);
      if (_executionHistory is not null && appliedPlan?.TargetNodeId is { } target &&
          _workflow.Nodes.FirstOrDefault(item => item.Id == target) is { } appliedNode) {
        await _executionHistory.AddAsync(ExecutionHistoryStore.Create(
            appliedNode,
            (response.Execution?.Outputs ?? []).ToDictionary(output => output.Port, output => output.Summary ?? "", StringComparer.Ordinal),
            response.Execution?.Timings.Sum(timing => timing.Duration.TotalMilliseconds) ?? 0));
      }
      var updated = GetAiContext(profile.Id)
          .Concat([new AiChatMessage("assistant", response.Message, AiChatMessageKinds.Execution)]).TakeLast(40).ToList();
      _aiContexts[AiContextKey(profile.Id)] = updated;
      await _aiContextStore.SaveAsync(_aiContexts);
      _pendingAiPlan = null;
      AiApplyButton.IsEnabled = false;
      RenderAiContext(profile);
    } catch (Exception exception) { AddAiMessage("assistant", _localization.Format("Ai.Error", UserError(exception))); } finally { SetAiBusy(false); }
  }

  private async void AiClearContext_Click(object? sender, RoutedEventArgs e) {
    if (AiModelComboBox.SelectedItem is not AiModelProfile profile) {
      return;
    }

    _aiContexts.Remove(AiContextKey(profile.Id));
    await _aiContextStore.SaveAsync(_aiContexts);
    RenderAiContext(profile);
  }

  private async Task EnsureAiProviderAsync(AiModelProfile profile) {
    if (_activeAiProfileId == profile.Id && _aiAgent is not null) {
      return;
    }

    var handle = await _aiProviderLifetime.GetOrCreateAsync(
        profile.Id,
        () => new AiProviderFactory().CreateAsync(
            profile, _automation!, AiSoftwarePaths.ConfigDirectory,
            typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "2.0.0"));
    _activeAiProfileId = profile.Id;
    _aiAgent = new AiWorkflowAgent(
        _automation!,
        handle.Provider,
        _project is null
            ? null
            : new OpenCvAiWorkflowTools(_registry, _project.RootPath, () => _workflow, series => _pendingSweep = series));
  }

  private async Task DisposeAiProviderAsync() {
    _aiAgent = null;
    _activeAiProfileId = null;
    await _aiProviderLifetime.ResetAsync();
  }

  private string? FindAiPreviewImage(AiImageReference? reference) {
    if (_project is null || reference is null) {
      return null;
    }

    string path = reference.RelativePath;
    if (string.IsNullOrWhiteSpace(path)) {
      return null;
    }

    try { string fullPath = ProjectPaths.ResolveInside(_project.RootPath, path); return File.Exists(fullPath) ? fullPath : null; } catch (InvalidOperationException) { return null; }
  }

  private void SetAiBusy(bool busy) {
    _aiBusy = busy;
    AiSendButton.IsEnabled = !busy;
    AiStopButton.IsVisible = busy;
    AiStopButton.IsEnabled = busy;
    AiModelComboBox.IsEnabled = !busy;
    if (busy) {
      AiApplyButton.IsEnabled = false;
    } else {
      AiApplyButton.IsEnabled = _pendingAiPlan is not null;
    }
  }

}
