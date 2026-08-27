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
        _aiProfiles.Add(new("__ui_test__", "UI Test OpenAI", "", null, "", Endpoint: testEndpoint,
            MaximumOutputTokens: 2048, Source: AiModelSource.Remote, ModelId: "ui-test", ProviderPluginId: ""));
      }

      AiModelComboBox.ItemsSource = null;
      AiModelComboBox.ItemsSource = _aiProfiles;
      var contexts = await _aiContextStore.LoadAsync();
      _aiContexts.Clear();
      foreach (var pair in contexts) {
        _aiContexts[pair.Key] = pair.Value;
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
      AddAiMessage(message.Role, message.Content);
    }

    string kind = profile.Source switch {
      AiModelSource.Remote => _localization.Text("Ai.RemoteModel"),
      AiModelSource.StandardLocal => _localization.Text("Ai.StandardModel"),
      _ => _localization.Text("Ai.LocalModel")
    };
    AiContextStatusText.Text = _localization.Format("Ai.ContextStatus", kind, GetAiContext(profile.Id).Count);
  }

  private string AiContextKey(string modelId) {
    return $"{modelId}::{_project?.Project.Id.ToString("N") ?? "global"}";
  }

  private IReadOnlyList<AiChatMessage> GetAiContext(string modelId) {
    return _aiContexts.TryGetValue(AiContextKey(modelId), out var messages) ? messages : [];
  }

  private void AddAiMessage(string role, string content) {
    var message = new ChatMessageControl(
        role, content,
        _localization.Text("Ai.You"), _localization.Text("Ai.Assistant"),
        _localization.Text("Ai.Quote"), _localization.Text("Ai.CopyCode"));
    message.QuoteRequested += (_, request) => InsertAiQuote(request.Text);
    AiConversationPanel.Children.Add(message);
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

  private async void AiSend_Click(object? sender, RoutedEventArgs e) {
    if (_automation is null || _project is null) { Status(_localization.Text("Status.ProjectRequired")); return; }
    if (AiModelComboBox.SelectedItem is not AiModelProfile profile) { AiContextStatusText.Text = _localization.Text("Ai.NoModels"); return; }
    string? text = AiPromptTextBox.Text?.Trim();
    if (string.IsNullOrWhiteSpace(text)) {
      return;
    }

    SetAiBusy(true);
    var history = GetAiContext(profile.Id);
    _pendingAiPlan = null;
    AiApplyButton.IsEnabled = false;
    _aiContexts[AiContextKey(profile.Id)] = [.. history.Concat([new AiChatMessage("user", text)]).TakeLast(40)];
    AiPromptTextBox.Text = "";
    await _aiContextStore.SaveAsync(_aiContexts);
    RenderAiContext(profile);
    try {
      await EnsureAiProviderAsync(profile);
      var snapshot = await _automation.GetSnapshotAsync();
      _pendingAiRevision = snapshot.Revision;
      var imageReferences = AiImageReferenceCatalog.Create(snapshot.Workflow);
      var imageReference = AiImageReferenceCatalog.Resolve(snapshot.Workflow, text, Canvas.SelectedNode?.Id);
      if (imageReferences.Count > 1 && imageReference is null) {
        throw new InvalidOperationException(_localization.IsChinese
            ? "流程中有多张输入图片，请在问题中写明 IMG 编号或名称，或把画布节点右上角的编号拖入对话框。"
            : "This workflow has multiple input images. Mention an IMG number/name or drag the badge from the canvas node into the prompt.");
      }

      string requestText = imageReference is null ? text : $"指定图像节点：{imageReference.PromptToken}（nodeId={imageReference.NodeId}）\n{text}";
      var response = await _aiAgent!.ProcessAsync(new(
          requestText, imageReference?.NodeId ?? Canvas.SelectedNode?.Id,
          FindAiPreviewImage(imageReference), AiAssistantMode.Suggest, history));
      var updated = GetAiContext(profile.Id).Concat([new AiChatMessage("assistant", response.Message)]).TakeLast(40).ToList();
      _aiContexts[AiContextKey(profile.Id)] = updated;
      await _aiContextStore.SaveAsync(_aiContexts);
      _pendingAiPlan = response.Plan;
      RenderAiContext(profile);
      AiApplyButton.IsEnabled = response.Plan is not null;
    } catch (Exception exception) {
      string error = _localization.Format("Ai.Error", UserError(exception));
      _aiContexts[AiContextKey(profile.Id)] = [.. GetAiContext(profile.Id)
          .Concat([new AiChatMessage("assistant", error)]).TakeLast(40)];
      await _aiContextStore.SaveAsync(_aiContexts);
      RenderAiContext(profile);
    } finally { SetAiBusy(false); }
  }

  private async void AiApply_Click(object? sender, RoutedEventArgs e) {
    if (_pendingAiPlan is null || _aiAgent is null || AiModelComboBox.SelectedItem is not AiModelProfile profile) {
      return;
    }

    SetAiBusy(true);
    try {
      var response = await _aiAgent.ApplyPlanAsync(_pendingAiPlan, _pendingAiRevision);
      var updated = GetAiContext(profile.Id).Concat([new AiChatMessage("assistant", response.Message)]).TakeLast(40).ToList();
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
    _aiAgent = new AiWorkflowAgent(_automation!, handle.Provider);
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
    AiSendButton.IsEnabled = !busy;
    AiModelComboBox.IsEnabled = !busy;
    if (busy) {
      AiApplyButton.IsEnabled = false;
    } else {
      AiApplyButton.IsEnabled = _pendingAiPlan is not null;
    }
  }

}
