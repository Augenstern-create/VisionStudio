using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VisionStudio.Core;

namespace VisionStudio.Runtimes.App;

public sealed partial class MainWindow : Window {
  private static readonly IReadOnlyList<RuntimeSourceOption> _sourceOptions = [
    new(RuntimeSourceKind.Image, "单张图片"),
    new(RuntimeSourceKind.ImageFolder, "图片文件夹"),
    new(RuntimeSourceKind.Video, "视频"),
    new(RuntimeSourceKind.Camera, "EmguCV 相机"),
    new(RuntimeSourceKind.Simulated, "模拟图")
  ];
  private static readonly IReadOnlyList<RuntimeTriggerOption> _triggerOptions = [
    new(RuntimeTriggerKind.Continuous, "连续采集"),
    new(RuntimeTriggerKind.Timer, "定时触发"),
    new(RuntimeTriggerKind.Manual, "手动/外部触发")
  ];
  private static readonly IReadOnlyList<RuntimeQueueOption> _queueOptions = [
    new(RuntimeQueueMode.ProcessAll, "全部处理"),
    new(RuntimeQueueMode.LatestFrame, "保留最新帧"),
    new(RuntimeQueueMode.DropWhenBusy, "繁忙时丢弃")
  ];
  private readonly ProjectRuntimeService _runtime = new();
  private readonly string? _initialProjectPath;
  private RuntimeProjectContext? _context;
  private ProjectRuntimeController? _controller;
  private Bitmap? _resultBitmap;

  public MainWindow() : this(null) {
  }

  public MainWindow(string? initialProjectPath) {
    InitializeComponent();
    _initialProjectPath = initialProjectPath;
    SourceKindCombo.ItemsSource = _sourceOptions;
    TriggerKindCombo.ItemsSource = _triggerOptions;
    QueueModeCombo.ItemsSource = _queueOptions;
    Opened += MainWindow_Opened;
  }

  private async void MainWindow_Opened(object? sender, EventArgs e) {
    if (!string.IsNullOrWhiteSpace(_initialProjectPath)) {
      await LoadProjectAsync(_initialProjectPath);
    }
  }

  private async void OpenProject_Click(object? sender, RoutedEventArgs e) {
    var files = await StorageProvider.OpenFilePickerAsync(
        new FilePickerOpenOptions {
          Title = "打开 Vision Studio 项目",
          AllowMultiple = false,
          FileTypeFilter = [new FilePickerFileType("Vision Studio project") {
            Patterns = [$"*{ProjectService.ProjectExtension}"]
          }]
        });
    if (files.Count > 0) {
      await LoadProjectAsync(files[0].Path.LocalPath);
    }
  }

  private async Task LoadProjectAsync(string path) {
    await DisposeControllerAsync();
    RunButton.IsEnabled = false;
    Status("正在加载项目……");
    try {
      _context = await _runtime.OpenAsync(path);
      var profile = _context.Profile;
      TitleText.Text = $"{_context.Project.Project.Name} · {profile.Name}";
      ProjectNameText.Text = _context.Project.Project.Name;
      ProjectPathText.Text = _context.Project.ProjectFilePath;
      NodeList.ItemsSource = BuildNodeItems(_context);
      ApplySourceSettings(profile.Source);
      ApplyExecutionSettings(profile.Execution);
      DiagnosticsText.Text = "";
      ClearResult();
      RunButton.IsEnabled = true;
      StartButton.IsEnabled = true;
      Status($"项目已加载：{_context.Project.ProjectFilePath}");
    } catch (Exception exception) {
      _context = null;
      DiagnosticsText.Text = exception.ToString();
      TitleText.Text = "项目加载失败";
      ProjectNameText.Text = "未加载项目";
      ProjectPathText.Text = "";
      NodeList.ItemsSource = null;
      StartButton.IsEnabled = false;
      Status($"项目加载失败：{exception.Message}");
    }
  }

  private void ApplyExecutionSettings(RuntimeExecutionSettings execution) {
    TriggerKindCombo.SelectedItem = _triggerOptions.Single(
        option => option.Kind == execution.Trigger);
    QueueModeCombo.SelectedItem = _queueOptions.Single(
        option => option.Mode == execution.QueueMode);
    IntervalText.Text = execution.IntervalMs.ToString();
    QueueCapacityText.Text = execution.QueueCapacity.ToString();
  }

  private void ApplySourceSettings(RuntimeSourceSettings source) {
    SourceKindCombo.SelectedItem = _sourceOptions.Single(option => option.Kind == source.Kind);
    SourceValueText.Text = source.Kind switch {
      RuntimeSourceKind.Camera => source.CameraIndex.ToString(),
      RuntimeSourceKind.Simulated => $"{source.Width}x{source.Height}",
      _ => source.Path
    };
  }

  private async void SaveProfile_Click(object? sender, RoutedEventArgs e) {
    if (_context is null) {
      return;
    }
    try {
      UpdateSourceSettings(_context.Profile.Source);
      UpdateExecutionSettings(_context.Profile.Execution);
      await _runtime.SaveProfileAsync(_context);
      DiagnosticsText.Text = "";
      Status("运行配置已保存到项目 runtime/profiles/default.runtime.json");
    } catch (Exception exception) {
      DiagnosticsText.Text = exception.ToString();
      Status($"保存运行配置失败：{exception.Message}");
    }
  }

  private async void Start_Click(object? sender, RoutedEventArgs e) {
    if (_context is null) {
      return;
    }
    try {
      UpdateSourceSettings(_context.Profile.Source);
      UpdateExecutionSettings(_context.Profile.Execution);
      await DisposeControllerAsync();
      _controller = new ProjectRuntimeController(_context, _runtime);
      _controller.StateChanged += Controller_StateChanged;
      _controller.ResultProduced += Controller_ResultProduced;
      _controller.RuntimeError += Controller_RuntimeError;
      _controller.StatisticsChanged += Controller_StatisticsChanged;
      ClearResult();
      await _controller.StartAsync();
      DiagnosticsText.Text = "";
      Status("连续运行已启动。");
    } catch (Exception exception) {
      DiagnosticsText.Text = exception.ToString();
      Status($"启动连续运行失败：{exception.Message}");
      await DisposeControllerAsync();
    }
  }

  private void Pause_Click(object? sender, RoutedEventArgs e) {
    if (_controller?.State == RuntimeState.Running) {
      _controller.Pause();
    } else if (_controller?.State == RuntimeState.Paused) {
      _controller.Resume();
    }
  }

  private void Trigger_Click(object? sender, RoutedEventArgs e) {
    if (_controller?.FireTrigger() == true) {
      Status("已提交一次手动触发。");
    }
  }

  private async void Stop_Click(object? sender, RoutedEventArgs e) {
    if (_controller is null) {
      return;
    }
    await _controller.StopAsync();
    Status("连续运行已停止。");
  }

  private async void Run_Click(object? sender, RoutedEventArgs e) {
    if (_context is null) {
      return;
    }
    RunButton.IsEnabled = false;
    ClearResult();
    Status("正在采集并执行工作流……");
    try {
      UpdateSourceSettings(_context.Profile.Source);
      var result = await _runtime.RunOnceAsync(_context);
      ShowResult(_context, result);
      DiagnosticsText.Text = "";
      Status($"运行完成，耗时 {result.ElapsedMilliseconds} ms");
    } catch (Exception exception) {
      DiagnosticsText.Text = exception.ToString();
      ResultStatusText.Text = "运行失败";
      Status($"运行失败：{exception.Message}");
    } finally {
      RunButton.IsEnabled = _context is not null;
    }
  }

  private void UpdateSourceSettings(RuntimeSourceSettings source) {
    source.Kind = SourceKindCombo.SelectedItem is RuntimeSourceOption selected
        ? selected.Kind
        : source.Kind;
    string value = SourceValueText.Text?.Trim() ?? "";
    switch (source.Kind) {
      case RuntimeSourceKind.Camera:
        if (!int.TryParse(value, out int cameraIndex) || cameraIndex < 0) {
          throw new ArgumentException("相机编号必须是非负整数。");
        }
        source.CameraIndex = cameraIndex;
        break;
      case RuntimeSourceKind.Simulated:
        string[] size = value.Split('x', 'X', '×');
        if (size.Length != 2
            || !int.TryParse(size[0], out int width)
            || !int.TryParse(size[1], out int height)) {
          throw new ArgumentException("模拟图尺寸应为 640x480 格式。");
        }
        source.Width = width;
        source.Height = height;
        break;
      default:
        if (string.IsNullOrWhiteSpace(value)) {
          throw new ArgumentException("请输入项目内的采集源路径。");
        }
        _ = ProjectPaths.ResolveInside(_context!.Project.RootPath, value);
        source.Path = value.Replace('\\', '/');
        break;
    }
  }

  private void UpdateExecutionSettings(RuntimeExecutionSettings execution) {
    execution.Trigger = TriggerKindCombo.SelectedItem is RuntimeTriggerOption trigger
        ? trigger.Kind
        : execution.Trigger;
    execution.QueueMode = QueueModeCombo.SelectedItem is RuntimeQueueOption queue
        ? queue.Mode
        : execution.QueueMode;
    if (!int.TryParse(IntervalText.Text, out int interval) || interval < 1) {
      throw new ArgumentException("触发间隔必须是大于 0 的整数毫秒。");
    }
    if (!int.TryParse(QueueCapacityText.Text, out int capacity)
        || capacity is < 1 or > 1024) {
      throw new ArgumentException("帧队列容量必须是 1 到 1024 的整数。");
    }
    execution.IntervalMs = interval;
    execution.QueueCapacity = capacity;
  }

  private void Controller_StateChanged(object? sender, RuntimeState state) {
    Dispatcher.UIThread.Post(() => {
      RuntimeStateText.Text = StateText(state);
      bool active = state is RuntimeState.Running or RuntimeState.Paused;
      StartButton.IsEnabled = !active && state != RuntimeState.Starting
          && state != RuntimeState.Stopping && _context is not null;
      PauseButton.IsEnabled = active;
      PauseButton.Content = state == RuntimeState.Paused ? "继续" : "暂停";
      StopButton.IsEnabled = active || state == RuntimeState.Starting;
      TriggerButton.IsEnabled = active
          && _context?.Profile.Execution.Trigger == RuntimeTriggerKind.Manual;
      RunButton.IsEnabled = !active && _context is not null;
    });
  }

  private void Controller_ResultProduced(
      object? sender,
      RuntimeInspectionResult result) {
    Dispatcher.UIThread.Post(() => {
      if (_context is not null) {
        ShowResult(_context, result);
        Status($"连续运行：第 {result.Sequence} 帧完成，耗时 {result.ElapsedMilliseconds} ms");
      }
    });
  }

  private void Controller_RuntimeError(object? sender, Exception exception) {
    Dispatcher.UIThread.Post(() => {
      DiagnosticsText.Text = exception.ToString();
      Status($"连续运行错误：{exception.Message}");
    });
  }

  private void Controller_StatisticsChanged(
      object? sender,
      RuntimeStatistics statistics) {
    Dispatcher.UIThread.Post(() => {
      RuntimeCompactStatisticsText.Text = $"采集 {statistics.Captured} · "
          + $"处理 {statistics.Processed} · 丢弃 {statistics.Dropped} · "
          + $"队列 {statistics.QueueDepth}/{statistics.QueueCapacity}";
      RuntimeStatisticsText.Text = $"已采集：{statistics.Captured}\n"
          + $"已处理：{statistics.Processed}\n"
          + $"已丢弃：{statistics.Dropped}\n"
          + $"错误数：{statistics.Errors}\n"
          + $"队列：{statistics.QueueDepth} / {statistics.QueueCapacity}";
    });
  }

  private async Task DisposeControllerAsync() {
    if (_controller is null) {
      return;
    }
    _controller.StateChanged -= Controller_StateChanged;
    _controller.ResultProduced -= Controller_ResultProduced;
    _controller.RuntimeError -= Controller_RuntimeError;
    _controller.StatisticsChanged -= Controller_StatisticsChanged;
    await _controller.DisposeAsync();
    _controller = null;
  }

  private static string StateText(RuntimeState state) {
    return state switch {
      RuntimeState.Ready => "就绪",
      RuntimeState.Starting => "正在启动",
      RuntimeState.Running => "运行中",
      RuntimeState.Paused => "已暂停",
      RuntimeState.Stopping => "正在停止",
      RuntimeState.Stopped => "已停止",
      RuntimeState.Faulted => "故障",
      _ => state.ToString()
    };
  }

  private void ShowResult(
      RuntimeProjectContext context,
      RuntimeInspectionResult result) {
    ResultValueList.ItemsSource = result.Values;
    TimingList.ItemsSource = result.Timings.Select(timing => new RuntimeTimingItem(
        NodeName(context, timing.NodeId),
        timing.Duration,
        timing.FromExternalInput));
    ResultStatusText.Text = "运行成功";
    CaptureInfoText.Text = $"来源：{result.SourceName}\n帧号：{result.Sequence}\n"
        + $"采集：{result.CapturedAt:yyyy-MM-dd HH:mm:ss.fff}\nRun ID：{result.RunId}";
    RunSummaryText.Text = $"{result.Values.Count} 项结果 · {result.ElapsedMilliseconds} ms";
    if (result.PrimaryImagePath is null) {
      return;
    }
    string path = ProjectPaths.ResolveInside(
        context.Project.RootPath,
        result.PrimaryImagePath);
    var bitmap = new Bitmap(path);
    var previous = _resultBitmap;
    _resultBitmap = bitmap;
    ResultImage.Source = bitmap;
    previous?.Dispose();
  }

  private void ClearResult() {
    ResultValueList.ItemsSource = null;
    TimingList.ItemsSource = null;
    ResultStatusText.Text = "尚未运行";
    CaptureInfoText.Text = "";
    RunSummaryText.Text = "";
    ResultImage.Source = null;
    _resultBitmap?.Dispose();
    _resultBitmap = null;
  }

  private static IReadOnlyList<RuntimeNodeItem> BuildNodeItems(
      RuntimeProjectContext context) {
    var validation = WorkflowValidator.Validate(
        context.Workflow,
        context.Registry);
    return [.. validation.TopologicalOrder.Select((node, index) => {
      context.Registry.TryGetDescriptor(node.Type, out var descriptor);
      string connector = index == validation.TopologicalOrder.Count - 1
          ? node.Id == context.Profile.TargetNodeId ? "● 运行目标" : ""
          : string.Join(
              "\n",
              context.Workflow.Connections
                  .Where(connection => connection.FromNode == node.Id)
                  .Select(connection =>
                      $"↓ {connection.FromPort} → {connection.ToPort}"));
      return new RuntimeNodeItem(
          node.Id,
          node.Name ?? descriptor.DisplayName,
          node.Type,
          connector);
    })];
  }

  private static string NodeName(RuntimeProjectContext context, string nodeId) {
    var node = context.Workflow.Nodes.Single(item => item.Id == nodeId);
    context.Registry.TryGetDescriptor(node.Type, out var descriptor);
    return node.Name ?? descriptor.DisplayName;
  }

  private void Status(string text) {
    StatusText.Text = text;
  }

  protected override async void OnClosed(EventArgs e) {
    await DisposeControllerAsync();
    _resultBitmap?.Dispose();
    base.OnClosed(e);
  }
}

public sealed record RuntimeNodeItem(
    string Id,
    string DisplayName,
    string Type,
    string ConnectorText);

public sealed record RuntimeTimingItem(
    string NodeName,
    TimeSpan Duration,
    bool FromExternalInput) {
  public string DurationText => FromExternalInput
      ? "外部输入"
      : $"{Duration.TotalMilliseconds:F2} ms";
}

public sealed record RuntimeSourceOption(
    RuntimeSourceKind Kind,
    string DisplayName);

public sealed record RuntimeTriggerOption(
    RuntimeTriggerKind Kind,
    string DisplayName);

public sealed record RuntimeQueueOption(
    RuntimeQueueMode Mode,
    string DisplayName);
