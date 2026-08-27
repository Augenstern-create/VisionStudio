using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using VisionStudio.Ai;
using VisionStudio.Ai.Abstractions;

namespace VisionStudio.App;

public sealed class SettingsWindow : Window {
  private readonly bool _chinese;
  private readonly AiModelProfileStore _store = new();
  private readonly LocalAiRuntimeManager _runtime = new();
  private readonly ObservableCollection<AiModelProfile> _localProfiles = [];
  private readonly ObservableCollection<AiModelProfile> _remoteProfiles = [];
  private readonly ListBox _localList = new();
  private readonly ListBox _remoteList = new();
  private readonly TextBox _localName = new();
  private readonly TextBox _localModelId = new();
  private readonly TextBox _localModelPath = new() { IsReadOnly = true };
  private readonly TextBox _localMmprojPath = new() { IsReadOnly = true };
  private readonly TextBlock _runtimeStatus = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .75 };
  private readonly TextBlock _standardInfo = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .72, FontSize = 11 };
  private readonly ComboBox _standardModels = new();
  private readonly TextBox _remoteName = new();
  private readonly TextBox _remoteModelId = new();
  private readonly TextBox _remoteEndpoint = new();
  private readonly TextBox _remoteApiKey = new() { PasswordChar = '●' };
  private readonly CheckBox _remoteVision = new();
  private IReadOnlyList<StandardAiModelDefinition> _standards = [];
  private AiModelProfile? _activeLocalProfile;
  private AiModelProfile? _activeRemoteProfile;
  private bool _loading;

  public SettingsWindow(bool chinese) {
    _chinese = chinese;
    Title = T("设置", "Settings"); Width = 940; Height = 670; MinWidth = 800; MinHeight = 580;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Content = BuildContent();
    _localList.ItemsSource = _localProfiles; _remoteList.ItemsSource = _remoteProfiles;
    _localList.SelectionChanged += (_, _) => ChangeLocalSelection();
    _remoteList.SelectionChanged += (_, _) => ChangeRemoteSelection();
    _standardModels.SelectionChanged += (_, _) => ShowStandardModelInfo();
    Opened += async (_, _) => await LoadAsync();
  }

  private Control BuildContent() {
    var tabs = new TabControl { Margin = new Thickness(14) };
    tabs.Items.Add(new TabItem { Header = T("本地模型", "Local Models"), Content = BuildLocalTab() });
    tabs.Items.Add(new TabItem { Header = T("远程模型", "Remote Models"), Content = BuildRemoteTab() });
    var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(14, 0, 14, 12) };
    footer.Children.Add(Button(T("确定", "OK"), Ok_Click));
    footer.Children.Add(Button(T("取消", "Cancel"), (_, _) => Close(false)));
    return new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { tabs, footer } }.Also(grid => Grid.SetRow(footer, 1));
  }

  private Control BuildLocalTab() {
    var root = new Grid { ColumnDefinitions = new ColumnDefinitions("255,*"), ColumnSpacing = 18, Margin = new Thickness(4, 12) };
    var left = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 8 };
    left.Children.Add(_localList);
    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
    buttons.Children.Add(Button(T("＋选择模型", "+ Select Model"), AddLocal_Click));
    buttons.Children.Add(Button(T("删除", "Delete"), DeleteLocal_Click));
    Grid.SetRow(buttons, 1); left.Children.Add(buttons); root.Children.Add(left);

    var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,*"), RowSpacing = 10 };
    var standard = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
    standard.Children.Add(new TextBlock { Text = T("标准版模型", "Standard model"), VerticalAlignment = VerticalAlignment.Center });
    Grid.SetColumn(_standardModels, 1); standard.Children.Add(_standardModels);
    var install = Button(T("下载并安装", "Download & Install"), InstallStandard_Click); Grid.SetColumn(install, 2); standard.Children.Add(install);
    right.Children.Add(standard);
    Grid.SetRow(_standardInfo, 1); right.Children.Add(_standardInfo);
    AddFormRow(right, 2, T("显示名称", "Display name"), _localName);
    AddFormRow(right, 3, T("接口模型 ID", "API model id"), _localModelId);
    AddPathRow(right, 4, T("GGUF 模型", "GGUF model"), _localModelPath, ChangeLocalModel_Click);
    AddPathRow(right, 5, T("视觉投影模型", "Vision projector"), _localMmprojPath, ChangeMmproj_Click);
    Grid.SetRow(_runtimeStatus, 6); right.Children.Add(_runtimeStatus);
    var note = new TextBlock {
      Text = T("llama-server 由软件自动检测；未安装时会从 llama.cpp 官方发布页下载到软件 runtime 目录，无需手动填写。",
            "llama-server is detected automatically. If absent, it is downloaded from official llama.cpp releases into the application runtime directory."),
      TextWrapping = Avalonia.Media.TextWrapping.Wrap,
      Opacity = .65,
      FontSize = 11
    };
    Grid.SetRow(note, 7); right.Children.Add(note);
    Grid.SetColumn(right, 1); root.Children.Add(right);
    return root;
  }

  private Control BuildRemoteTab() {
    var root = new Grid { ColumnDefinitions = new ColumnDefinitions("255,*"), ColumnSpacing = 18, Margin = new Thickness(4, 12) };
    var left = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 8 };
    left.Children.Add(_remoteList);
    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
    buttons.Children.Add(Button(T("＋远程模型", "+ Remote Model"), AddRemote_Click));
    buttons.Children.Add(Button(T("删除", "Delete"), DeleteRemote_Click));
    Grid.SetRow(buttons, 1); left.Children.Add(buttons); root.Children.Add(left);

    var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*"), RowSpacing = 10 };
    AddFormRow(right, 0, T("显示名称", "Display name"), _remoteName);
    AddFormRow(right, 1, T("接口模型 ID", "API model id"), _remoteModelId);
    AddFormRow(right, 2, "OpenAI Base URL", _remoteEndpoint);
    AddFormRow(right, 3, "API Key", _remoteApiKey);
    _remoteVision.Content = T("接口支持图片输入", "Endpoint supports image input"); Grid.SetRow(_remoteVision, 4); Grid.SetColumn(_remoteVision, 1); right.Children.Add(_remoteVision);
    var note = new TextBlock {
      Text = T($"远程配置独立保存在：{AiSoftwarePaths.RemoteProfilesFile}\n名称、端点、模型 ID、API Key 与能力参数整体加密，JSON 中不会出现明文。",
            $"Remote settings: {AiSoftwarePaths.RemoteProfilesFile}\nNames, endpoints, model IDs, API keys and capability parameters are encrypted as one payload."),
      TextWrapping = Avalonia.Media.TextWrapping.Wrap,
      Opacity = .65,
      FontSize = 11
    };
    Grid.SetRow(note, 5); Grid.SetColumnSpan(note, 2); right.Children.Add(note);
    Grid.SetColumn(right, 1); root.Children.Add(right);
    return root;
  }

  private async Task LoadAsync() {
    _loading = true;
    try {
      foreach (var profile in await _store.LoadLocalAsync()) {
        _localProfiles.Add(profile);
      }

      foreach (var profile in await _store.LoadRemoteAsync()) {
        _remoteProfiles.Add(profile);
      }

      _standards = await _store.LoadStandardModelsAsync(); _standardModels.ItemsSource = _standards;
      if (_standards.Count > 0) {
        _standardModels.SelectedIndex = 0;
      }
      ShowStandardModelInfo();

      if (_localProfiles.Count > 0) {
        _localList.SelectedIndex = 0;
      }

      if (_remoteProfiles.Count > 0) {
        _remoteList.SelectedIndex = 0;
      }

      RefreshRuntimeStatus();
    } finally {
      _loading = false;
      LoadLocalEditor();
      LoadRemoteEditor();
    }
  }

  private void ShowStandardModelInfo() {
    if (_standardModels.SelectedItem is not StandardAiModelDefinition standard) {
      _standardInfo.Text = T("没有可安装的标准模型。", "No standard model is available.");
      return;
    }
    double totalGiB = ((standard.ModelSizeBytes ?? 0) + (standard.MmprojSizeBytes ?? 0))
        / 1024d / 1024d / 1024d;
    _standardInfo.Text = T(
        $"来源：{standard.SourceUrl}\n许可证：{standard.License} · 下载约 {totalGiB:F2} GiB · 安装前后校验 SHA-256",
        $"Source: {standard.SourceUrl}\nLicense: {standard.License} · Download: about {totalGiB:F2} GiB · SHA-256 verified before installation");
  }

  private async void AddLocal_Click(object? sender, RoutedEventArgs e) {
    string model = await PickFileAsync(T("选择 GGUF 模型", "Select GGUF model"), "*.gguf", null);
    if (string.IsNullOrWhiteSpace(model)) {
      return;
    }

    var profile = new AiModelProfile(Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(model), model, null, "",
        Source: AiModelSource.Local, ModelId: Path.GetFileNameWithoutExtension(model), SupportsImageInput: false);
    await AddAndPrepareLocalAsync(profile, null);
  }

  private async void InstallStandard_Click(object? sender, RoutedEventArgs e) {
    if (_standardModels.SelectedItem is not StandardAiModelDefinition standard) {
      return;
    }

    var profile = AiModelProfileStore.CreateStandardProfile(standard) with { Id = _localProfiles.Any(item => item.Id == standard.Id) ? standard.Id + "-" + Guid.NewGuid().ToString("N")[..6] : standard.Id };
    await AddAndPrepareLocalAsync(profile, standard);
  }

  private async Task AddAndPrepareLocalAsync(AiModelProfile profile, StandardAiModelDefinition? standard) {
    try {
      SetLocalBusy(true);
      var progress = new Progress<string>(message => _runtimeStatus.Text = message);
      var prepared = await _runtime.PrepareProfileAsync(profile, standard, progress);
      _localProfiles.Add(prepared); _localList.SelectedItem = prepared;
      await SaveAllAsync(); RefreshRuntimeStatus();
    } catch (Exception exception) { await ShowErrorAsync(exception.Message); } finally { SetLocalBusy(false); }
  }

  private void LoadLocalEditor() {
    if (_loading || _localList.SelectedItem is not AiModelProfile profile) {
      return;
    }

    _activeLocalProfile = profile;
    _localName.Text = profile.DisplayName; _localModelId.Text = profile.EffectiveModelId;
    _localModelPath.Text = profile.ModelPath; _localMmprojPath.Text = profile.MmprojPath ?? "";
  }
  private void LoadRemoteEditor() {
    if (_loading || _remoteList.SelectedItem is not AiModelProfile profile) {
      return;
    }

    _activeRemoteProfile = profile;
    _remoteName.Text = profile.DisplayName; _remoteModelId.Text = profile.EffectiveModelId;
    _remoteEndpoint.Text = profile.Endpoint; _remoteApiKey.Text = profile.ApiKey ?? ""; _remoteVision.IsChecked = profile.SupportsImageInput;
  }

  private void ChangeLocalSelection() {
    if (_loading) {
      return;
    }

    SaveLocalEditor(validate: false);
    LoadLocalEditor();
  }

  private void ChangeRemoteSelection() {
    if (_loading) {
      return;
    }

    SaveRemoteEditor(validate: false);
    LoadRemoteEditor();
  }

  private void SaveEditors() {
    SaveLocalEditor(validate: true);
    SaveRemoteEditor(validate: true);
  }

  private void SaveLocalEditor(bool validate) {
    if (_activeLocalProfile is not AiModelProfile local) {
      return;
    }

    if (validate) {
      Require(_localName.Text, T("本地模型显示名称不能为空。", "Local model display name is required."));
      Require(_localModelId.Text, T("本地模型 ID 不能为空。", "Local model id is required."));
    }
    if (string.IsNullOrWhiteSpace(_localName.Text) || string.IsNullOrWhiteSpace(_localModelId.Text)) {
      return;
    }

    var updated = local with {
      DisplayName = _localName.Text.Trim(),
      ModelId = _localModelId.Text.Trim(),
      ModelPath = _localModelPath.Text ?? "",
      MmprojPath = Empty(_localMmprojPath.Text),
      SupportsImageInput = !string.IsNullOrWhiteSpace(_localMmprojPath.Text)
    };
    ReplaceProfile(_localProfiles, local, updated);
    _activeLocalProfile = updated;
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
    }
    if (string.IsNullOrWhiteSpace(_remoteName.Text) || string.IsNullOrWhiteSpace(_remoteModelId.Text) || string.IsNullOrWhiteSpace(_remoteEndpoint.Text)) {
      return;
    }

    var updated = remote with {
      DisplayName = _remoteName.Text.Trim(),
      ModelId = _remoteModelId.Text.Trim(),
      Endpoint = _remoteEndpoint.Text.Trim(),
      ApiKey = Empty(_remoteApiKey.Text),
      SupportsImageInput = _remoteVision.IsChecked == true
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
      bool selected = ReferenceEquals(_localList.SelectedItem, previous) || ReferenceEquals(_remoteList.SelectedItem, previous);
      profiles[index] = updated;
      if (selected) {
        if (ReferenceEquals(_localList.ItemsSource, profiles)) {
          _localList.SelectedItem = updated;
        }

        if (ReferenceEquals(_remoteList.ItemsSource, profiles)) {
          _remoteList.SelectedItem = updated;
        }
      }
    } finally { _loading = false; }
  }

  private void AddRemote_Click(object? sender, RoutedEventArgs e) {
    var profile = new AiModelProfile(Guid.NewGuid().ToString("N"), T("远程模型", "Remote Model"), "", null, "",
        Endpoint: "https://api.openai.com/v1", Source: AiModelSource.Remote, ModelId: "gpt-4.1-mini", ProviderPluginId: "");
    _remoteProfiles.Add(profile); _remoteList.SelectedItem = profile;
  }
  private void DeleteLocal_Click(object? sender, RoutedEventArgs e) {
    if (_localList.SelectedItem is not AiModelProfile value) {
      return;
    }

    if (ReferenceEquals(_activeLocalProfile, value)) {
      _activeLocalProfile = null;
    }

    _localProfiles.Remove(value);
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
  private async void ChangeLocalModel_Click(object? sender, RoutedEventArgs e) {
    _localModelPath.Text = await PickFileAsync(T("选择 GGUF 模型", "Select GGUF model"), "*.gguf", _localModelPath.Text);
  }

  private async void ChangeMmproj_Click(object? sender, RoutedEventArgs e) {
    _localMmprojPath.Text = await PickFileAsync(T("选择视觉投影模型", "Select vision projector"), "*.gguf", _localMmprojPath.Text);
  }

  private async void Ok_Click(object? sender, RoutedEventArgs e) {
    try { SaveEditors(); await SaveAllAsync(); Close(true); } catch (Exception exception) { await ShowErrorAsync(exception.Message); }
  }
  private Task SaveAllAsync() {
    return _store.SaveAsync([.. _localProfiles, .. _remoteProfiles]);
  }

  private void SetLocalBusy(bool busy) { _standardModels.IsEnabled = !busy; _localList.IsEnabled = !busy; }
  private void RefreshRuntimeStatus() {
    string? server = _runtime.FindLlamaServer();
    string? plugin = new AiPluginLoader().DiscoverDirectories(AiModelProfileStore.DefaultPluginRoots())
        .FirstOrDefault(directory => {
          try { return File.ReadAllText(Path.Combine(directory, "plugin.json")).Contains("visionstudio.ai.llamacpp", StringComparison.Ordinal); } catch (IOException) { return false; }
        });
    string runtimeLine = server is null
        ? T("llama-server：未检测到（安装模型时自动下载）", "llama-server: not found (downloaded when installing a model)")
        : "llama-server：" + server;
    string pluginLine = plugin is null
        ? T("连接插件：未安装（llama-server 不能替代 Provider 插件）", "Connector plugin: not installed (llama-server does not replace the Provider plugin)")
        : T("连接插件：", "Connector plugin: ") + plugin;
    _runtimeStatus.Text = runtimeLine + Environment.NewLine + pluginLine;
  }

  private async Task<string> PickFileAsync(string title, string pattern, string? previous) {
    var result = await StorageProvider.OpenFilePickerAsync(new() {
      Title = title,
      AllowMultiple = false,
      FileTypeFilter = [new FilePickerFileType(pattern) { Patterns = [pattern] }]
    });
    return result.FirstOrDefault()?.TryGetLocalPath() ?? previous ?? "";
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
  private static void AddPathRow(Grid grid, int row, string label, TextBox box, EventHandler<RoutedEventArgs> click) {
    var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("145,*,Auto"), ColumnSpacing = 8 };
    panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(box, 1); panel.Children.Add(box);
    var browse = Button("…", click); Grid.SetColumn(browse, 2); panel.Children.Add(browse); Grid.SetRow(panel, row); grid.Children.Add(panel);
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

  private string T(string chinese, string english) {
    return _chinese ? chinese : english;
  }
}

internal static class ControlConstructionExtensions {
  public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
