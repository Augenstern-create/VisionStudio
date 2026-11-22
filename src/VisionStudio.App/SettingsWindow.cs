using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using VisionStudio.Ai;
using VisionStudio.Ai.Abstractions;

namespace VisionStudio.App;

public sealed class SettingsWindow : Window {
  private readonly bool _chinese;
  private readonly AiModelProfileStore _store = new();
  private readonly ObservableCollection<AiModelProfile> _remoteProfiles = [];
  private readonly ListBox _remoteList = new();
  private readonly TextBox _remoteName = new();
  private readonly TextBox _remoteModelId = new();
  private readonly TextBox _remoteEndpoint = new();
  private readonly TextBox _remoteApiKey = new() { PasswordChar = '●' };
  private readonly TextBox _remoteMaxTokens = new();
  private readonly TextBox _remoteMaxInput = new();
  private readonly ComboBox _remoteEffort = new();
  private readonly TextBlock _testStatus = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .8, FontSize = 11 };
  private readonly CheckBox _remoteVision = new();
  private sealed record EffortOption(AiReasoningEffort Value, string Label) {
    public override string ToString() => Label;
  }
  private AiModelProfile? _activeRemoteProfile;
  private bool _loading;

  public SettingsWindow(bool chinese) {
    _chinese = chinese;
    Title = T("设置", "Settings"); Width = 860; Height = 600; MinWidth = 720; MinHeight = 520;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Content = BuildContent();
    _remoteList.ItemsSource = _remoteProfiles;
    _remoteList.SelectionChanged += (_, _) => ChangeRemoteSelection();
    Opened += async (_, _) => await LoadAsync();
  }

  private Control BuildContent() {
    var body = new Grid { Margin = new Thickness(14), ColumnDefinitions = new ColumnDefinitions("255,*"), ColumnSpacing = 18 };
    var heading = new TextBlock {
      Text = T("远程模型（OpenAI 兼容接口）", "Remote models (OpenAI-compatible)"),
      FontWeight = Avalonia.Media.FontWeight.SemiBold,
      Margin = new Thickness(2, 0, 0, 8)
    };
    var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
    left.Children.Add(heading);
    Grid.SetRow(_remoteList, 1); left.Children.Add(_remoteList);
    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
    buttons.Children.Add(Button(T("＋远程模型", "+ Remote Model"), AddRemote_Click));
    buttons.Children.Add(Button(T("删除", "Delete"), DeleteRemote_Click));
    Grid.SetRow(buttons, 2); left.Children.Add(buttons);
    body.Children.Add(left);

    var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,*"), RowSpacing = 10 };
    AddFormRow(right, 0, T("显示名称", "Display name"), _remoteName);
    AddFormRow(right, 1, T("接口模型 ID", "API model id"), _remoteModelId);
    AddFormRow(right, 2, "OpenAI Base URL", _remoteEndpoint);
    AddFormRow(right, 3, "API Key", _remoteApiKey);
    AddFormRow(right, 4, T("思考等级", "Reasoning effort"), _remoteEffort);
    AddFormRow(right, 5, T("最大输入 token（如 1M）", "Max input tokens (e.g. 1M)"), _remoteMaxInput);
    AddFormRow(right, 6, T("最大输出 token（如 8K）", "Max output tokens (e.g. 8K)"), _remoteMaxTokens);
    _remoteVision.Content = T("接口支持图片输入", "Endpoint supports image input");
    Grid.SetRow(_remoteVision, 7); Grid.SetColumn(_remoteVision, 1); right.Children.Add(_remoteVision);
    var note = new TextBlock {
      Text = T($"配置保存在：{AiSoftwarePaths.RemoteProfilesFile}\n名称、端点、模型 ID、API Key 与能力参数整体加密，JSON 中不会出现明文。\nVision Studio 只支持远程模型，不再内置本地推理运行时。\n“思考等级：自动”不发送任何参数；“关闭”发送 thinking.type=disabled；“低/高/最高”发送 reasoning_effort。\n“最大输入 token”用于裁剪对话历史与算子上下文，“最大输出 token”即 max_tokens；推理模型请把输出 token 调到 4096 以上。",
            $"Settings: {AiSoftwarePaths.RemoteProfilesFile}\nNames, endpoints, model IDs, API keys and capability parameters are encrypted as one payload.\nVision Studio supports remote models only; the local inference runtime has been removed.\nReasoning effort auto sends nothing, off sends thinking.type=disabled, low/high/max send reasoning_effort.\nMax input tokens bounds the trimmed history and operator context; max output tokens is max_tokens (use 4K+ for reasoning models). Both token fields accept 128K / 1M (K=1024, M=1048576)."),
      TextWrapping = Avalonia.Media.TextWrapping.Wrap,
      Opacity = .65,
      FontSize = 11,
      VerticalAlignment = VerticalAlignment.Top
    };
    Grid.SetRow(note, 8); Grid.SetColumnSpan(note, 2); right.Children.Add(note);
    Grid.SetColumn(right, 1); body.Children.Add(right);

    var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(14, 0, 14, 12) };
    footer.Children.Add(Button(T("测试连接", "Test connection"), Test_Click));
    footer.Children.Add(Button(T("确定", "OK"), Ok_Click));
    footer.Children.Add(Button(T("取消", "Cancel"), (_, _) => Close(false)));
    var bottom = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(14, 0, 14, 0) };
    _testStatus.Margin = new Thickness(0, 0, 0, 6);
    bottom.Children.Add(_testStatus);
    Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 2); bottom.Children.Add(footer);
    return new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { body, bottom } }.Also(grid => Grid.SetRow(bottom, 1));
  }

  private async Task LoadAsync() {
    _loading = true;
    try {
      foreach (var profile in await _store.LoadRemoteAsync()) {
        _remoteProfiles.Add(profile);
      }

      if (_remoteProfiles.Count > 0) {
        _remoteList.SelectedIndex = 0;
      }
    } finally {
      _loading = false;
      LoadRemoteEditor();
    }
  }

  private void LoadRemoteEditor() {
    if (_loading || _remoteList.SelectedItem is not AiModelProfile profile) {
      return;
    }

    _activeRemoteProfile = profile;
    _remoteName.Text = profile.DisplayName; _remoteModelId.Text = profile.EffectiveModelId;
    _remoteEndpoint.Text = profile.Endpoint; _remoteApiKey.Text = profile.ApiKey ?? ""; _remoteVision.IsChecked = profile.SupportsImageInput;
    _remoteMaxTokens.Text = AiTokenCount.Format(profile.MaximumOutputTokens);
    _remoteMaxInput.Text = AiTokenCount.Format(profile.MaxInputTokens);
    _remoteEffort.ItemsSource = EffortOptions;
    _remoteEffort.SelectedItem = EffortOptions.First(option => option.Value == profile.ReasoningEffort);
  }

  private void ChangeRemoteSelection() {
    if (_loading) {
      return;
    }

    SaveRemoteEditor(validate: false);
    LoadRemoteEditor();
  }

  private void SaveEditors() {
    SaveRemoteEditor(validate: true);
  }

  private void SaveRemoteEditor(bool validate) {
    if (_activeRemoteProfile is not AiModelProfile remote) {
      return;
    }

    if (validate) {
      Require(_remoteName.Text, T("远程模型显示名称不能为空。", "Remote model display name is required."));
      Require(_remoteModelId.Text, T("远程模型 ID 不能为空。", "Remote model id is required."));
      Require(_remoteEndpoint.Text, T("OpenAI Base URL 不能为空。", "OpenAI Base URL is required."));
      if (!Uri.TryCreate(_remoteEndpoint.Text, UriKind.Absolute, out var endpoint) ||
          (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)) {
        throw new InvalidOperationException(T("OpenAI Base URL 必须是有效的 HTTP 或 HTTPS 地址。", "OpenAI Base URL must be a valid HTTP or HTTPS address."));
      }
      if (!AiTokenCount.TryParse(_remoteMaxTokens.Text, out int parsedTokens) || parsedTokens <= 0) {
        throw new InvalidOperationException(T(
            "最大输出 token 需为数字，或 8K / 1M 这类写法（K=1024，M=1048576）。",
            "Max output tokens must be a number or a value such as 8K / 1M (K=1024, M=1048576)."));
      }
      if (!AiTokenCount.TryParse(_remoteMaxInput.Text, out int parsedInput) || parsedInput < 1000) {
        throw new InvalidOperationException(T(
            "最大输入 token 需为数字，或 128K / 1M 这类写法，且不小于 1000。",
            "Max input tokens must be a number or a value such as 128K / 1M, at least 1000."));
      }
      if (parsedInput <= parsedTokens) {
        throw new InvalidOperationException(T("最大输入 token 必须大于最大输出 token。", "Max input tokens must exceed max output tokens."));
      }
    }
    if (string.IsNullOrWhiteSpace(_remoteName.Text) || string.IsNullOrWhiteSpace(_remoteModelId.Text) || string.IsNullOrWhiteSpace(_remoteEndpoint.Text)) {
      return;
    }

    var updated = remote with {
      DisplayName = _remoteName.Text.Trim(),
      ModelId = _remoteModelId.Text.Trim(),
      Endpoint = _remoteEndpoint.Text.Trim(),
      ApiKey = Empty(_remoteApiKey.Text),
      SupportsImageInput = _remoteVision.IsChecked == true,
      MaximumOutputTokens = AiTokenCount.TryParse(_remoteMaxTokens.Text, out int tokens) && tokens > 0 ? tokens : remote.MaximumOutputTokens,
      MaxInputTokens = AiTokenCount.TryParse(_remoteMaxInput.Text, out int inputTokens) && inputTokens >= 1000 ? inputTokens : remote.MaxInputTokens,
      ReasoningEffort = (_remoteEffort.SelectedItem as EffortOption)?.Value ?? remote.ReasoningEffort
    };
    ReplaceProfile(_remoteProfiles, remote, updated);
    _activeRemoteProfile = updated;
  }

  private void ReplaceProfile(ObservableCollection<AiModelProfile> profiles, AiModelProfile previous, AiModelProfile updated) {
    int index = profiles.IndexOf(previous);
    if (index < 0) {
      return;
    }

    _loading = true;
    try {
      bool selected = ReferenceEquals(_remoteList.SelectedItem, previous);
      profiles[index] = updated;
      if (selected) {
        _remoteList.SelectedItem = updated;
      }
    } finally { _loading = false; }
  }

  private void AddRemote_Click(object? sender, RoutedEventArgs e) {
    var profile = new AiModelProfile(Guid.NewGuid().ToString("N"), T("远程模型", "Remote Model"),
        "https://api.openai.com/v1", "gpt-4.1-mini", MaximumOutputTokens: 4096, MaxInputTokens: 65536);
    _remoteProfiles.Add(profile); _remoteList.SelectedItem = profile;
  }

  private void DeleteRemote_Click(object? sender, RoutedEventArgs e) {
    if (_remoteList.SelectedItem is not AiModelProfile value) {
      return;
    }

    if (ReferenceEquals(_activeRemoteProfile, value)) {
      _activeRemoteProfile = null;
    }

    _remoteProfiles.Remove(value);
  }

  private async void Test_Click(object? sender, RoutedEventArgs e) {
    SaveRemoteEditor(validate: false);
    if (_activeRemoteProfile is not AiModelProfile profile) {
      _testStatus.Text = T("请先选择或新建一个远程模型。", "Select or create a remote model first.");
      return;
    }

    _testStatus.Text = T("正在连接…", "Connecting…");
    var probe = profile with { MaximumOutputTokens = Math.Min(Math.Max(profile.MaximumOutputTokens, 512), 512) };
    await using var provider = new OpenAiCompatibleProvider(probe);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    try {
      var response = await Task.Run(() => provider.SendAsync(new(
          "You are a connectivity probe. Reply with exactly one short word.", [new("user", "ping")], Temperature: 0)));
      _testStatus.Text = T(
          $"连接成功 · {watch.ElapsedMilliseconds} ms · 端点 {OpenAiCompatibleProvider.ResolveChatCompletionsUri(profile.Endpoint)} · 返回：{Trim(response.Content, 60)}",
          $"Connected · {watch.ElapsedMilliseconds} ms · {OpenAiCompatibleProvider.ResolveChatCompletionsUri(profile.Endpoint)} · reply: {Trim(response.Content, 60)}");
    } catch (InvalidDataException exception) when (exception.Message.Contains("empty message content", StringComparison.OrdinalIgnoreCase)) {
      _testStatus.Text = T(
          $"端点可达，但返回正文为空（推理模型把预算用在思考上）。建议把最大输出 token 调到 4K 以上。\n{Trim(exception.Message, 200)}",
          $"Endpoint reachable but the answer body was empty (reasoning used the budget). Raise max output tokens to 4K+.\n{Trim(exception.Message, 200)}");
    } catch (Exception exception) {
      _testStatus.Text = Trim(exception.Message, 400);
    }
  }

  private static string Trim(string text, int maximum) {
    string flat = text.ReplaceLineEndings(" ");
    return flat.Length <= maximum ? flat : flat[..maximum] + "…";
  }

  private async void Ok_Click(object? sender, RoutedEventArgs e) {
    try { SaveEditors(); await _store.SaveAsync([.. _remoteProfiles]); Close(true); } catch (Exception exception) { await ShowErrorAsync(exception.Message); }
  }

  private async Task ShowErrorAsync(string message) {
    var dialog = new Window { Title = T("设置错误", "Settings Error"), Width = 480, Height = 190, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    var close = Button(T("关闭", "Close"), (_, _) => dialog.Close());
    dialog.Content = new StackPanel {
      Margin = new Thickness(20),
      Spacing = 16,
      Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, close }
    };
    await dialog.ShowDialog(this);
  }
  private static void AddFormRow(Grid grid, int row, string label, Control control) {
    var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("145,*"), ColumnSpacing = 8 };
    panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(control, 1); panel.Children.Add(control);
    Grid.SetRow(panel, row); grid.Children.Add(panel);
  }
  private static Button Button(string text, EventHandler<RoutedEventArgs> click) { var value = new Button { Content = text }; value.Click += click; return value; }
  private static void Require(string? value, string message) {
    if (string.IsNullOrWhiteSpace(value)) {
      throw new InvalidOperationException(message);
    }
  }
  private static string? Empty(string? value) {
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
  }

  private IReadOnlyList<EffortOption> EffortOptions => _effortOptions ??= [
    new(AiReasoningEffort.Auto, T("自动（不发送参数）", "Auto (send nothing)")),
    new(AiReasoningEffort.Off, T("关闭思考", "Off")),
    new(AiReasoningEffort.Low, T("低", "Low")),
    new(AiReasoningEffort.High, T("高", "High")),
    new(AiReasoningEffort.Max, T("最高", "Max"))
  ];
  private IReadOnlyList<EffortOption>? _effortOptions;

  private string T(string chinese, string english) {
    return _chinese ? chinese : english;
  }
}

internal static class ControlConstructionExtensions {
  public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
