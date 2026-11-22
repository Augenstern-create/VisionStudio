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
  private readonly OperatorRegistry _registry = OpenCvOperatorCatalog.CreateRegistry();
  private readonly UiLocalization _localization = new();
  private readonly UiThemeManager _themes = new();
  private readonly ProjectService _projects = new();
  private readonly Stack<UndoEntry> _undoStack = new();
  private readonly ObservableCollection<string> _operationItems = [];
  private ProjectHandle? _project;
  private WorkflowDocument _workflow = new();
  private bool _dirty;
  private Bitmap? _preview;
  private string? _previewDigest;
  private readonly HashSet<Bitmap> _retiredPreviews = new(ReferenceEqualityComparer.Instance);
  private CancellationTokenSource? _autosaveCancellation;
  private CancellationTokenSource? _previewCancellation;
  private readonly DispatcherTimer _runTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
  private DateTime _runStarted;
  private int _runIndex;
  private int _runTotal;
  private string? _runNodeLabel;
  private double _previewZoom = 1;
  private bool _previewFit = true;
  private WorkflowAutomationSession? _automation;
  private ExecutionHistoryStore? _executionHistory;
  private readonly AiModelProfileStore _aiModelStore = new();
  private readonly AiConversationContextStore _aiContextStore = new();
  private readonly List<AiModelProfile> _aiProfiles = [];
  private readonly Dictionary<string, IReadOnlyList<AiChatMessage>> _aiContexts = new(StringComparer.Ordinal);
  private readonly AiProviderLifetime _aiProviderLifetime = new();
  private string? _activeAiProfileId;
  private AiWorkflowAgent? _aiAgent;
  private AiWorkflowPlan? _pendingAiPlan;
  private long _pendingAiRevision;
  private bool _closeCleanupStarted;
  private bool _allowClose;

  public IWorkflowAutomationApi? WorkflowAutomationApi => _automation;
  private WorkflowAutomationApiHost AutomationHost => ((App)Application.Current!).WorkflowAutomation;

  public MainWindow() : this(null) { }

  public MainWindow(string? initialProjectPath) {
    InitializeComponent();
    _runTimer.Tick += (_, _) => RefreshRunStatus();
    _aiTimer.Tick += OnAiTick;
    Canvas.Registry = _registry;
    Canvas.DisplayNameSelector = descriptor => _localization.OperatorName(descriptor);
    Canvas.PortNameSelector = port => _localization.IsChinese ? port.DisplayName ?? port.Name : port.Name;
    Canvas.ConnectionNameSelector = connection => _localization.Format("Canvas.Connection", connection.FromPort, connection.ToPort);
    Canvas.NodeReferenceSelector = node => AiImageReferenceCatalog.Create(_workflow).FirstOrDefault(item => item.NodeId == node.Id)?.Reference;
    Canvas.Workflow = _workflow;
    _themes.Apply(Application.Current!, _themes.Current, persist: false);
    Canvas.ApplyTheme(_themes.Current);
    _localization.LanguageChanged += (_, _) => ApplyLanguage();
    OperationList.ItemsSource = _operationItems;
    PreviewScroll.SizeChanged += (_, _) => { if (_previewFit) { ApplyPreviewScale(); } };
    Canvas.SelectionChanged += (_, node) => {
      ShowProperties(node);
      SchedulePreview(node);
    };
    Canvas.WorkflowChanged += (_, change) => {
      MarkDirty();
      RefreshAiImageReferences();
      if (change.AffectsExecution) {
        SchedulePreview(Canvas.SelectedNode);
      }
    };
    Canvas.WorkflowChanging += (_, change) => RecordUndo(change.Description);
    BuildPalette();
    ApplyLanguage();
    SwitchActivity(WorkbenchActivity.Operators);
    if (!string.IsNullOrWhiteSpace(initialProjectPath)) {
      Opened += async (_, _) => await LoadProjectAsync(initialProjectPath);
    }
  }

  private void BuildPalette() {
    PalettePanel.Children.Clear();
    string? query = OperatorSearchBox.Text?.Trim();
    var descriptors = string.IsNullOrWhiteSpace(query)
        ? _registry.Descriptors
        : _registry.Descriptors.Where(descriptor =>
            _localization.OperatorName(descriptor).Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            descriptor.Type.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            _localization.CategoryName(descriptor.Category).Contains(query, StringComparison.CurrentCultureIgnoreCase));

    bool firstCategory = true;
    foreach (var category in descriptors.GroupBy(descriptor => descriptor.Category).OrderBy(group => CategoryRank(group.Key))) {
      if (!firstCategory) {
        PalettePanel.Children.Add(new Border { Height = 1, Background = ResourceBrush("BorderBrush"), Margin = new Thickness(10, 0) });
      }

      firstCategory = false;

      var items = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 0, 4) };
      foreach (var descriptor in category) {
        var button = new Button {
          Content = _localization.OperatorName(descriptor),
          Tag = descriptor,
          HorizontalAlignment = HorizontalAlignment.Stretch,
          HorizontalContentAlignment = HorizontalAlignment.Left,
          Classes = { "operatorItem" }
        };
        button.Click += AddOperator_Click;
        items.Children.Add(button);
      }
      var header = new TextBlock {
        Text = _localization.CategoryName(category.Key),
        FontSize = 12,
        FontWeight = Avalonia.Media.FontWeight.SemiBold,
        Foreground = ResourceBrush("TextBrush"),
        VerticalAlignment = VerticalAlignment.Center
      };
      var expander = new Expander {
        Header = header,
        IsExpanded = category.Key == "输入" || !string.IsNullOrWhiteSpace(query),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Content = items
      };
      expander.Classes.Add("paletteCategory");
      AutomationProperties.SetName(expander, $"{_localization.Text("Panel.Operators")}：{_localization.CategoryName(category.Key)}");
      PalettePanel.Children.Add(expander);
    }
  }

  private static int CategoryRank(string category) {
    return category switch {
      "输入" => 0,
      "图像" => 1,
      "基础处理" => 2,
      "滤波" => 3,
      "增强" => 4,
      "阈值" => 5,
      "二值处理" => 6,
      "形态学" => 7,
      "边缘" => 8,
      "分割" => 9,
      "Blob" => 10,
      "轮廓" => 11,
      "几何" => 12,
      "定位" => 13,
      "直线/圆" => 14,
      "变换" => 15,
      "图像运算" => 16,
      "统计" => 17,
      "测量" => 18,
      "缺陷检测" => 19,
      "标定" => 20,
      "逻辑" => 21,
      "集合" => 22,
      "显示" => 23,
      "AI" => 24,
      "输出" => 25,
      _ => 100
    };
  }

  private void OperatorSearchBox_TextChanged(object? sender, TextChangedEventArgs e) {
    BuildPalette();
  }

  private void HistoryMenuItem_Click(object? sender, RoutedEventArgs e) {
    HistoryPanel.IsVisible = HistoryMenuItem.IsChecked;
  }

  private void OperatorsActivity_Click(object? sender, RoutedEventArgs e) {
    SwitchActivity(WorkbenchActivity.Operators);
  }


  private void SwitchActivity(WorkbenchActivity activity) {
    bool showOperators = activity == WorkbenchActivity.Operators;
    bool showAi = activity == WorkbenchActivity.Ai;
    OperatorSidebar.IsVisible = showOperators;
    AiSidebar.IsVisible = showAi;
    WorkflowWorkspace.IsVisible = true;
    WorkflowInspector.IsVisible = true;
    OperatorSearchBox.IsVisible = showOperators;
    OperatorsActivityButton.Classes.Set("selected", showOperators);
    AiActivityButton.Classes.Set("selected", showAi);
    EditorView.ColumnDefinitions[1].Width = new GridLength(showAi ? 340 : 280);
    EditorView.ColumnDefinitions[5].Width = new GridLength(356);
    if (showAi) {
      RefreshAiImageReferences();
    }
  }























  private enum WorkbenchActivity { Operators, Ai }

  private void AddOperator_Click(object? sender, RoutedEventArgs e) {
    if (sender is not Button { Tag: OperatorDescriptor descriptor }) {
      return;
    }

    RecordUndo($"添加算子：{_localization.OperatorName(descriptor)}");
    var position = Canvas.FindAvailableNodePosition();
    _workflow.Nodes.Add(OpenCvOperatorCatalog.CreateNode(descriptor, position.X, position.Y));
    MarkDirty();
    Canvas.Refresh();
    Status(_localization.Text("Status.OperatorAdded"));
  }








  private void AutoLayout_Click(object? sender, RoutedEventArgs e) {
    if (Canvas.AutoLayout()) {
      Status(_localization.Text("Status.AutoLayout"));
    }
  }

































  private void EnterEditor() {
    foreach (var bitmap in _retiredPreviews) {
      bitmap.Dispose();
    }

    _retiredPreviews.Clear();
    WelcomeView.IsVisible = false;
    EditorView.IsVisible = true;
  }













  protected override void OnKeyDown(KeyEventArgs e) {
    if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
      UndoLastOperation();
      e.Handled = true;
      return;
    }
    base.OnKeyDown(e);
  }

  protected override void OnClosing(WindowClosingEventArgs e) {
    if (!_allowClose) {
      e.Cancel = true;
      if (!_closeCleanupStarted) {
        _closeCleanupStarted = true;
        _autosaveCancellation?.Cancel();
        CancelPreview();
        _ = CompleteCloseAsync();
      }
    }
    base.OnClosing(e);
  }

  private async Task CompleteCloseAsync() {
    try {
      await DetachAutomationSessionAsync();
      await _aiProviderLifetime.DisposeAsync();
    } catch (Exception exception) {
      Debug.WriteLine($"AI provider cleanup failed: {exception}");
    } finally {
      _allowClose = true;
      Dispatcher.UIThread.Post(Close);
    }
  }

  protected override void OnClosed(EventArgs e) {
    _preview?.Dispose();
    foreach (var bitmap in _retiredPreviews) {
      bitmap.Dispose();
    }

    _retiredPreviews.Clear();
    _autosaveCancellation?.Cancel(); _autosaveCancellation?.Dispose();
    CancelPreview();
    base.OnClosed(e);
  }

  private sealed record UndoEntry(string WorkflowJson, string Description);
}
