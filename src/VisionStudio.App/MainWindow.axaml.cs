using System.Globalization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Emgu.CV;
using VisionStudio.CodeGen;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.App;

public sealed partial class MainWindow : Window
{
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
    private double _previewZoom = 1;
    private bool _previewFit = true;
    private WorkflowAutomationSession? _automation;

    public IWorkflowAutomationApi? WorkflowAutomationApi => _automation;
    private WorkflowAutomationApiHost AutomationHost => ((App)Application.Current!).WorkflowAutomation;

    public MainWindow() : this(null) { }

    public MainWindow(string? initialProjectPath)
    {
        InitializeComponent();
        Canvas.Registry = _registry;
        Canvas.DisplayNameSelector = descriptor => _localization.OperatorName(descriptor);
        Canvas.PortNameSelector = port => _localization.IsChinese ? port.DisplayName ?? port.Name : port.Name;
        Canvas.ConnectionNameSelector = connection => _localization.Format("Canvas.Connection", connection.FromPort, connection.ToPort);
        Canvas.Workflow = _workflow;
        _themes.Apply(Application.Current!, _themes.Current, persist: false);
        Canvas.ApplyTheme(_themes.Current);
        _localization.LanguageChanged += (_, _) => ApplyLanguage();
        OperationList.ItemsSource = _operationItems;
        PreviewScroll.SizeChanged += (_, _) => { if (_previewFit) ApplyPreviewScale(); };
        Canvas.SelectionChanged += (_, node) =>
        {
            ShowProperties(node);
            SchedulePreview(node);
        };
        Canvas.WorkflowChanged += (_, change) =>
        {
            MarkDirty();
            if (change.AffectsExecution) SchedulePreview(Canvas.SelectedNode);
        };
        Canvas.WorkflowChanging += (_, change) => RecordUndo(change.Description);
        BuildPalette();
        ApplyLanguage();
        if (!string.IsNullOrWhiteSpace(initialProjectPath))
            Opened += async (_, _) => await LoadProjectAsync(initialProjectPath);
    }

    private void BuildPalette()
    {
        PalettePanel.Children.Clear();
        var query = OperatorSearchBox.Text?.Trim();
        var descriptors = string.IsNullOrWhiteSpace(query)
            ? _registry.Descriptors
            : _registry.Descriptors.Where(descriptor =>
                _localization.OperatorName(descriptor).Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                descriptor.Type.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                _localization.CategoryName(descriptor.Category).Contains(query, StringComparison.CurrentCultureIgnoreCase));

        var firstCategory = true;
        foreach (var category in descriptors.GroupBy(descriptor => descriptor.Category).OrderBy(group => CategoryRank(group.Key)))
        {
            if (!firstCategory)
                PalettePanel.Children.Add(new Border { Height = 1, Background = ResourceBrush("BorderBrush"), Margin = new Thickness(10, 0) });
            firstCategory = false;

            var items = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 0, 4) };
            foreach (var descriptor in category)
            {
                var button = new Button
                {
                    Content = _localization.OperatorName(descriptor), Tag = descriptor,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Classes = { "operatorItem" }
                };
                button.Click += AddOperator_Click;
                items.Children.Add(button);
            }
            var header = new TextBlock
            {
                Text = _localization.CategoryName(category.Key),
                FontSize = 12,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = ResourceBrush("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var expander = new Expander
            {
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

    private static int CategoryRank(string category) => category switch
    {
        "输入" => 0, "图像" => 1, "基础处理" => 2, "滤波" => 3, "增强" => 4,
        "阈值" => 5, "二值处理" => 6, "形态学" => 7, "边缘" => 8, "分割" => 9,
        "Blob" => 10, "轮廓" => 11, "几何" => 12, "定位" => 13, "直线/圆" => 14,
        "变换" => 15, "图像运算" => 16, "统计" => 17, "测量" => 18, "标定" => 19,
        "逻辑" => 20, "集合" => 21, "显示" => 22, "AI" => 23, "输出" => 24,
        _ => 100
    };

    private void OperatorSearchBox_TextChanged(object? sender, TextChangedEventArgs e) => BuildPalette();

    private void HistoryMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        HistoryPanel.IsVisible = HistoryMenuItem.IsChecked;
    }

    private void LanguageMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string language } && language != _localization.CurrentLanguage)
            _localization.SetLanguage(language);
    }

    private void ThemeMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } || !Enum.TryParse<UiThemeKind>(value, true, out var theme)) return;
        _themes.Apply(Application.Current!, theme);
        Canvas.ApplyTheme(theme);
        SyncThemeChecks();
        BuildPalette();
        ShowProperties(Canvas.SelectedNode);
    }

    private void ApplyLanguage()
    {
        WelcomeSubtitleText.Text = _localization.Text("Welcome.Subtitle");
        WelcomeTabText.Text = _localization.Text("Welcome.Tab");
        WelcomeSectionText.Text = _localization.Text("Welcome.Section");
        WelcomeFeatureOneText.Text = _localization.Text("Welcome.Feature1");
        WelcomeFeatureTwoText.Text = _localization.Text("Welcome.Feature2");
        WelcomeFeatureThreeText.Text = _localization.Text("Welcome.Feature3");
        NewProjectTitleText.Text = _localization.Text("Welcome.NewProject");
        NewProjectDescriptionText.Text = _localization.Text("Welcome.NewProjectDescription");
        OpenProjectTitleText.Text = _localization.Text("Welcome.OpenProject");
        OpenProjectDescriptionText.Text = _localization.Text("Welcome.OpenProjectDescription");
        WelcomeStatusText.Text = _localization.Text("Welcome.InitialStatus");
        FileMenuItem.Header = _localization.Text("Toolbar.File");
        QuickActionsMenuItem.Header = _localization.Text("Toolbar.QuickActions");
        ViewMenuItem.Header = _localization.Text("Toolbar.View");
        HelpMenuItem.Header = _localization.Text("Toolbar.Help");
        SaveMenuItem.Header = _localization.Text("Toolbar.Save");
        ExportMenuItem.Header = _localization.Text("Toolbar.Export");
        CloseFolderMenuItem.Header = _localization.Text("Toolbar.CloseFolder");
        UndoMenuItem.Header = _localization.Text("Toolbar.Undo");
        AutoLayoutMenuItem.Header = _localization.Text("Toolbar.AutoLayout");
        AboutMenuItem.Header = _localization.Text("Toolbar.About");
        WelcomeViewMenuItem.Header = _localization.Text("Toolbar.View");
        WelcomeLanguageMenuItem.Header = LanguageMenuItem.Header = _localization.Text("View.Language");
        WelcomeThemeMenuItem.Header = ThemeMenuItem.Header = _localization.Text("View.Theme");
        WelcomeChineseMenuItem.Header = ChineseMenuItem.Header = _localization.Text("Language.Chinese");
        WelcomeEnglishMenuItem.Header = EnglishMenuItem.Header = _localization.Text("Language.English");
        WelcomeDarkThemeMenuItem.Header = DarkThemeMenuItem.Header = _localization.Text("Theme.Dark");
        WelcomeLightThemeMenuItem.Header = LightThemeMenuItem.Header = _localization.Text("Theme.Light");
        WelcomeGradientThemeMenuItem.Header = GradientThemeMenuItem.Header = _localization.Text("Theme.Gradient");
        ChineseMenuItem.IsChecked = WelcomeChineseMenuItem.IsChecked = _localization.IsChinese;
        EnglishMenuItem.IsChecked = WelcomeEnglishMenuItem.IsChecked = !_localization.IsChinese;
        SyncThemeChecks();
        HistoryMenuItem.Header = _localization.Text("Panel.History");
        OperatorsTitleText.Text = _localization.Text("Panel.Operators");
        ResultTitleText.Text = _localization.Text("Panel.Result");
        PreviewFitButton.Content = _localization.Text("Panel.Fit");
        PropertiesTitleText.Text = _localization.Text("Panel.Properties");
        HistoryTitleText.Text = _localization.Text("Panel.History");
        OperatorSearchBox.PlaceholderText = _localization.Text("Panel.SearchOperators");
        AutomationProperties.SetName(OperatorSearchBox, _localization.Text("Panel.SearchOperators"));
        PreviewEmptyText.Text = _localization.Text("Panel.EmptyPreview");
        PropertyEmptyText.Text = _localization.Text("Panel.EmptyProperties");
        if (Canvas.SelectedNode is null) StatusText.Text = _localization.Text("Panel.InitialResult");
        AutomationProperties.SetName(Canvas, _localization.Text("Canvas.AutomationName"));
        WorkflowLabelText.Text = _localization.Text("Canvas.Workflow");
        BuildPalette();
        Canvas.Refresh();
        ShowProperties(Canvas.SelectedNode);
        if (_project is not null) _ = LoadOperationHistoryAsync();
    }

    private void AddOperator_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OperatorDescriptor descriptor }) return;
        RecordUndo($"添加算子：{_localization.OperatorName(descriptor)}");
        var position = Canvas.FindAvailableNodePosition();
        _workflow.Nodes.Add(OpenCvOperatorCatalog.CreateNode(descriptor, position.X, position.Y));
        MarkDirty();
        Canvas.Refresh();
        Status(_localization.Text("Status.OperatorAdded"));
    }

    private async void NewProject_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = _localization.Text("Picker.ProjectLocation"), AllowMultiple = false });
        var parent = folders.FirstOrDefault()?.TryGetLocalPath();
        if (parent is null) return;
        var name = await AskProjectNameAsync();
        if (string.IsNullOrWhiteSpace(name)) return;
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        var path = Path.Combine(parent, name);
        try
        {
            _project = await _projects.CreateAsync(path, name);
            await new RecentProjectCatalog().RecordAsync(_project);
            _workflow = await _projects.LoadWorkflowAsync(_project);
            ResetUndoHistory();
            SetWorkflow();
            EnterEditor();
            await LoadOperationHistoryAsync();
            RecordOperation("创建项目", _project.RootPath);
            Status(_localization.Text("Status.ProjectCreated"));
        }
        catch (Exception ex) { WelcomeStatus(UserError(ex)); }
    }

    private async void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localization.Text("Picker.OpenProject"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(_localization.Text("Picker.ProjectFiles")) { Patterns = [$"*{ProjectService.ProjectExtension}"] }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await LoadProjectAsync(path);
    }

    private async void CloseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_dirty && !await SaveAsync()) return;
        CancelPreview();
        _autosaveCancellation?.Cancel();
        // Allow the native Invoke/Click provider to finish before its source button
        // menu is removed from the visible automation and visual trees.
        await Task.Delay(50);
        WelcomeView.IsVisible = true;
        EditorView.IsVisible = false;
        ReleasePreviewsAfterNavigation();
        _project = null;
        DetachAutomationSession();
        _workflow = new WorkflowDocument();
        Canvas.Workflow = _workflow;
        Canvas.Refresh();
        ResetUndoHistory();
        _operationItems.Clear();
        HistoryMenuItem.IsChecked = false;
        HistoryPanel.IsVisible = false;
        ProjectText.Text = "";
        UpdateTitle();
        WelcomeView.Focus();
        WelcomeStatus(_localization.Text("Status.WelcomeAgain"));
    }

    private async void About_Click(object? sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var close = new Button { Content = _localization.Text("About.Close"), Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = _localization.Text("About.Title"), Width = 470, Height = 300, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(28),
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new Border
                            {
                                Width = 52, Height = 52, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left,
                                Background = ResourceBrush("PrimaryBrush"),
                                Child = new TextBlock { Text = "V", FontSize = 27, FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = Avalonia.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                            },
                            new TextBlock { Text = "Vision Studio", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                            new TextBlock { Text = _localization.Text("About.Description"), Foreground = ResourceBrush("MutedTextBrush") },
                            new TextBlock { Text = _localization.Format("About.Version", version), FontSize = 12, Opacity = .72 },
                            new TextBlock { Text = _localization.Text("About.Technology"), FontSize = 12, Opacity = .72, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                        }
                    },
                    close
                }
            }
        };
        Grid.SetRow(close, 1);
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async Task LoadProjectAsync(string path)
    {
        try
        {
            _project = await _projects.OpenAsync(path);
            await new RecentProjectCatalog().RecordAsync(_project);
            _workflow = await _projects.LoadWorkflowAsync(_project);
            ResetUndoHistory();
            SetWorkflow();
            EnterEditor();
            await LoadOperationHistoryAsync();
            RecordOperation("打开项目", _project.RootPath);
            Status(_localization.Text("Status.ProjectOpened"));
        }
        catch (Exception ex) { WelcomeStatus(UserError(ex)); }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveAsync();

    private void AutoLayout_Click(object? sender, RoutedEventArgs e)
    {
        if (Canvas.AutoLayout()) Status(_localization.Text("Status.AutoLayout"));
    }

    private void PreviewFit_Click(object? sender, RoutedEventArgs e)
    {
        _previewFit = true;
        ApplyPreviewScale();
    }

    private void PreviewZoomIn_Click(object? sender, RoutedEventArgs e) => SetPreviewZoom(_previewZoom * 1.2);
    private void PreviewZoomOut_Click(object? sender, RoutedEventArgs e) => SetPreviewZoom(_previewZoom / 1.2);

    private void Preview_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_preview is null) return;
        SetPreviewZoom(_previewZoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15));
        e.Handled = true;
    }

    private async Task<bool> SaveAsync()
    {
        if (_project is null) { Status(_localization.Text("Status.ProjectRequired")); return false; }
        try
        {
            await _projects.SaveWorkflowAsync(_project, _workflow);
            _dirty = false;
            UpdateTitle();
            Status(_localization.Text("Status.ProjectSaved"));
            return true;
        }
        catch (Exception ex) { Status(UserError(ex)); return false; }
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (_project is null) { Status(_localization.Text("Status.ProjectRequired")); return; }
        try
        {
            var path = ProjectPaths.ResolveInside(_project.RootPath, "output/GeneratedVisionAlgorithm.cs");
            var outputDirectory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(path, new CSharpWorkflowGenerator(_registry).Generate(_workflow));
            RecordOperation("导出 C#", "output/GeneratedVisionAlgorithm.cs");
            try
            {
                if (!string.Equals(Environment.GetEnvironmentVariable("VISIONSTUDIO_DISABLE_OPEN_FOLDER"), "1", StringComparison.Ordinal))
                    Process.Start(new ProcessStartInfo { FileName = outputDirectory, UseShellExecute = true });
                Status(_localization.Format("Status.ExportedOpened", path));
            }
            catch (Exception openError) { Status(_localization.Format("Status.ExportedOpenFailed", path, UserError(openError))); }
        }
        catch (Exception ex) { Status(UserError(ex)); }
    }

    private void ShowProperties(WorkflowNode? node)
    {
        PropertyPanel.Children.Clear();
        if (node is null || !_registry.TryGetDescriptor(node.Type, out var descriptor))
        {
            PropertyEmptyState.IsVisible = true;
            PropertyPanel.IsVisible = false;
            return;
        }
        PropertyEmptyState.IsVisible = false;
        PropertyPanel.IsVisible = true;

        var identity = new StackPanel { Spacing = 5 };
        identity.Children.Add(new TextBlock { Text = _localization.OperatorName(descriptor), FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        identity.Children.Add(new TextBlock { Text = _localization.Format("Property.Interface", descriptor.Type), Opacity = .72, FontSize = 12 });
        identity.Children.Add(new TextBlock { Text = _localization.Format("Property.Category", _localization.CategoryName(descriptor.Category)), Opacity = .6, FontSize = 11 });
        var operatorDescription = _localization.OperatorDescription(descriptor);
        if (!string.IsNullOrWhiteSpace(operatorDescription))
            identity.Children.Add(new TextBlock { Text = operatorDescription, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .78, Margin = new Thickness(0, 3, 0, 0) });
        PropertyPanel.Children.Add(new Border
        {
            Background = ResourceBrush("InspectorCardBrush"), BorderBrush = ResourceBrush("BorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(11), Child = identity,
            Margin = new Thickness(0, 0, 0, 6)
        });

        foreach (var parameter in descriptor.Parameters)
        {
            var parameterName = _localization.IsChinese ? parameter.DisplayName : parameter.Name;
            PropertyPanel.Children.Add(new TextBlock { Text = parameterName, Margin = new Thickness(0, 8, 0, 0), FontWeight = Avalonia.Media.FontWeight.SemiBold });
            if (_localization.IsChinese && !string.IsNullOrWhiteSpace(parameter.Description))
                PropertyPanel.Children.Add(new TextBlock { Text = parameter.Description, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .62, FontSize = 12 });
            var current = node.Parameters.TryGetValue(parameter.Name, out var element) ? element.ToString() : parameter.DefaultValue.ToString() ?? "";
            var editor = new TextBox { Text = current, Tag = parameter };
            AutomationProperties.SetName(editor, parameterName);
            editor.LostFocus += (_, _) => ApplyParameter(node, editor);
            PropertyPanel.Children.Add(editor);
            if (node.Type == "Image.Load" && parameter.Name == "path")
            {
                var browse = new Button { Content = _localization.Text("Property.ImportImage"), HorizontalAlignment = HorizontalAlignment.Stretch };
                browse.Click += async (_, _) => await ChooseInputImageAsync(node);
                PropertyPanel.Children.Add(browse);
            }
        }

        if (descriptor.Inputs.Count > 0)
        {
            PropertyPanel.Children.Add(new TextBlock { Text = _localization.Text("Property.Inputs"), FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
            foreach (var port in descriptor.Inputs)
                PropertyPanel.Children.Add(new TextBlock { Text = $"● {(_localization.IsChinese ? port.DisplayName ?? port.Name : port.Name)}：{(_localization.IsChinese ? port.Description ?? PortTypeText(port.Type) : port.Type.ToString())}", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .7 });
        }
        if (descriptor.Outputs.Count > 0)
        {
            PropertyPanel.Children.Add(new TextBlock { Text = _localization.Text("Property.Outputs"), FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            foreach (var port in descriptor.Outputs)
                PropertyPanel.Children.Add(new TextBlock { Text = $"● {(_localization.IsChinese ? port.DisplayName ?? port.Name : port.Name)}：{(_localization.IsChinese ? port.Description ?? PortTypeText(port.Type) : port.Type.ToString())}", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .7 });
        }
    }

    private async Task ChooseInputImageAsync(WorkflowNode node)
    {
        if (_project is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localization.Text("Picker.ImportImage"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(_localization.Text("Picker.ImageFiles")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff", "*.webp"] }]
        });
        var source = files.FirstOrDefault()?.TryGetLocalPath();
        if (source is null) return;
        try
        {
            var relative = await _projects.ImportInputImageAsync(_project, source);
            RecordUndo("修改输入图片");
            node.SetParameter("path", relative);
            MarkDirty();
            ShowProperties(node);
            SchedulePreview(node);
            Status(_localization.Format("Status.ImageImported", relative));
        }
        catch (Exception ex) { Status(UserError(ex)); }
    }

    private void ApplyParameter(WorkflowNode node, TextBox editor)
    {
        if (editor.Tag is not ParameterDefinition parameter) return;
        try
        {
            object value = parameter.Kind switch
            {
                JsonValueKind.Number when parameter.DefaultValue is int => int.Parse(editor.Text ?? "", CultureInfo.InvariantCulture),
                JsonValueKind.Number => double.Parse(editor.Text ?? "", CultureInfo.InvariantCulture),
                JsonValueKind.True or JsonValueKind.False => bool.Parse(editor.Text ?? ""),
                _ => editor.Text ?? ""
            };
            if (value is IConvertible convertible && parameter.Kind == JsonValueKind.Number)
            {
                var number = convertible.ToDouble(CultureInfo.InvariantCulture);
                if (parameter.Minimum is { } min && number < min || parameter.Maximum is { } max && number > max) throw new ArgumentOutOfRangeException(parameter.Name);
            }
            var serialized = JsonSerializer.SerializeToElement(value, WorkflowJson.Options);
            if (node.Parameters.TryGetValue(parameter.Name, out var existing) && existing.GetRawText() == serialized.GetRawText()) return;
            RecordUndo($"修改参数：{parameter.DisplayName}");
            node.SetParameter(parameter.Name, value);
            MarkDirty();
            SchedulePreview(node);
        }
        catch (Exception ex) { Status(_localization.Format("Status.InvalidParameter", UserError(ex))); ShowProperties(node); }
    }

    private void SchedulePreview(WorkflowNode? node)
    {
        CancelPreview();
        if (node is null || _project is null) return;
        var cancellation = _previewCancellation = new CancellationTokenSource();
        _ = PreviewAfterDelayAsync(node, cancellation.Token);
    }

    private void CancelPreview()
    {
        var cancellation = _previewCancellation;
        _previewCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task PreviewAfterDelayAsync(WorkflowNode node, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);
            Status(_localization.Format("Status.PreviewLoading", DisplayName(node)));
            using var result = await new WorkflowExecutor(_registry, _project!.RootPath, new OpenCvProjectCache(_project.RootPath))
                .ExecuteAsync(_workflow, node.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = _registry.Descriptors.Single(x => x.Type == node.Type);
            var imagePort = descriptor.Outputs.FirstOrDefault(x => x.Type == NodeValueType.Mat);
            if (imagePort is null || !result.TryGetOutput<Mat>(node.Id, imagePort.Name, out var mat) || mat is null)
            {
                var values = DescribeOutputs(result.GetNodeOutputs(node.Id));
                Status(values.Length == 0 ? _localization.Text("Status.NoPreviewOutput") : _localization.Format("Status.ResultValues", _localization.OperatorName(descriptor), values));
                return;
            }
            var previewPath = ProjectPaths.ResolveInside(_project.RootPath, ".project/autosave/preview.png");
            OpenCvFileCodec.Write(previewPath, mat);
            var previewBytes = await File.ReadAllBytesAsync(previewPath, cancellationToken);
            var digest = Convert.ToHexString(SHA256.HashData(previewBytes));
            if (string.Equals(digest, _previewDigest, StringComparison.Ordinal))
            {
                var cachedUnchanged = result.Timings.Count(x => x.FromCache);
                Status(_localization.Format("Status.ResultUnchanged", _localization.OperatorName(descriptor), cachedUnchanged, result.Timings.Count));
                return;
            }
            using var stream = new MemoryStream(previewBytes, writable: false);
            var bitmap = new Bitmap(stream);
            if (cancellationToken.IsCancellationRequested)
            {
                bitmap.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            ReplacePreview(bitmap, digest);
            var cached = result.Timings.Count(x => x.FromCache);
            var elapsed = result.Timings.Sum(x => x.Duration.TotalMilliseconds);
            var summary = DescribeOutputs(result.GetNodeOutputs(node.Id));
            var outputSummary = summary.Length == 0 ? "" : _localization.Format("Status.OutputSummary", summary);
            Status(_localization.Format("Status.ResultShown", _localization.OperatorName(descriptor), outputSummary, cached, result.Timings.Count, elapsed));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status(_localization.Format("Status.PreviewFailed", UserError(ex))); }
    }

    private void ReplacePreview(Bitmap bitmap, string digest)
    {
        var previous = _preview;
        _preview = bitmap;
        _previewDigest = digest;
        PreviewImage.Source = bitmap;
        PreviewEmptyState.IsVisible = false;
        _previewFit = true;
        ApplyPreviewScale();
        // Keep replaced frames alive while the editor is visible. Avalonia's render
        // thread can retain an older frame beyond the UI dispatcher turn.
        if (previous is not null) _retiredPreviews.Add(previous);
    }

    private void SetPreviewZoom(double zoom)
    {
        _previewFit = false;
        _previewZoom = Math.Clamp(zoom, .05, 16);
        ApplyPreviewScale();
    }

    private void ApplyPreviewScale()
    {
        if (_preview is null) return;
        if (_previewFit)
        {
            var availableWidth = Math.Max(1, PreviewScroll.Bounds.Width - 6);
            var availableHeight = Math.Max(1, PreviewScroll.Bounds.Height - 6);
            _previewZoom = Math.Clamp(Math.Min(availableWidth / _preview.PixelSize.Width, availableHeight / _preview.PixelSize.Height), .05, 16);
        }
        PreviewImage.Width = Math.Max(1, _preview.PixelSize.Width * _previewZoom);
        PreviewImage.Height = Math.Max(1, _preview.PixelSize.Height * _previewZoom);
    }

    private string DescribeOutputs(IReadOnlyDictionary<string, object> outputs) => string.Join(_localization.IsChinese ? "；" : "; ", outputs
        .Where(pair => pair.Value is not Mat)
        .Select(pair => $"{pair.Key}={pair.Value switch { System.Collections.ICollection list => _localization.Format("Value.Collection", list.Count), _ => pair.Value }}")
        .Take(6));

    private void ReleasePreviewsAfterNavigation()
    {
        PreviewImage.Source = null;
        PreviewEmptyState.IsVisible = true;
        if (_preview is not null) _retiredPreviews.Add(_preview);
        _preview = null;
        _previewDigest = null;
        PreviewImage.Width = double.NaN;
        PreviewImage.Height = double.NaN;
    }

    private void SetWorkflow()
    {
        AttachAutomationSession();
        Canvas.Workflow = _workflow;
        Canvas.Refresh();
        _dirty = false;
        ProjectText.Text = _project?.Project.Name ?? "";
        ToolTip.SetTip(ProjectText, _project?.ProjectFilePath ?? "");
        UpdateTitle();
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => UndoLastOperation();

    private void UndoLastOperation()
    {
        if (_undoStack.Count == 0) { Status(_localization.Text("Status.NoUndo")); return; }
        var entry = _undoStack.Pop();
        CancelPreview();
        _workflow = WorkflowJson.Deserialize(entry.WorkflowJson);
        _automation?.SynchronizeFromHost(_workflow);
        Canvas.Workflow = _workflow;
        Canvas.Refresh();
        ShowProperties(null);
        MarkDirty(notifyAutomation: false);
        RecordOperation("撤销", entry.Description);
        Status(_localization.Format("Status.Undone", TranslateOperationText(entry.Description)));
    }

    private void RecordUndo(string description)
    {
        _undoStack.Push(new UndoEntry(WorkflowJson.Serialize(_workflow), description));
        TrimUndoHistory();
        RecordOperation(description);
    }

    private void TrimUndoHistory()
    {
        while (_undoStack.Count > 100)
        {
            var retained = _undoStack.Take(100).Reverse().ToArray();
            _undoStack.Clear();
            foreach (var item in retained) _undoStack.Push(item);
        }
    }

    private void ResetUndoHistory() => _undoStack.Clear();

    private void RecordOperation(string action, string? detail = null)
    {
        var operation = new ProjectOperation(DateTimeOffset.Now, action, detail);
        _operationItems.Insert(0, FormatOperation(operation));
        while (_operationItems.Count > 200) _operationItems.RemoveAt(_operationItems.Count - 1);
        if (_project is not null)
        {
            _ = AppendOperationSafelyAsync(operation);
            ScheduleAutosave();
        }
    }

    private async Task AppendOperationSafelyAsync(ProjectOperation operation)
    {
        try { await _projects.AppendOperationAsync(_project!, operation); }
        catch (Exception ex) { Status(_localization.Format("Status.OperationLogFailed", UserError(ex))); }
    }

    private async Task LoadOperationHistoryAsync()
    {
        _operationItems.Clear();
        if (_project is null) return;
        foreach (var operation in (await _projects.LoadOperationsAsync(_project)).Reverse())
            _operationItems.Add(FormatOperation(operation));
    }

    private string FormatOperation(ProjectOperation operation)
    {
        var action = TranslateOperationText(operation.Action);
        var detail = string.IsNullOrWhiteSpace(operation.Detail) ? "" : TranslateOperationText(operation.Detail);
        if (!_localization.IsChinese && ContainsHan(detail)) detail = "";
        return $"{operation.Timestamp:HH:mm:ss}  {action}{(detail.Length == 0 ? "" : " — " + detail)}";
    }

    private string TranslateOperationText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || _localization.IsChinese) return value ?? "";
        var exact = value switch
        {
            "创建项目" => "Action.CreateProject", "打开项目" => "Action.OpenProject", "导出 C#" => "Action.ExportCode",
            "修改输入图片" => "Action.ImportImage", "撤销" => "Action.Undo", "删除连接" => "Action.DeleteConnection",
            "创建连接" => "Action.CreateConnection", "移动算子" => "Action.MoveOperator", "删除算子" => "Action.DeleteOperator",
            "AI 工作流修改" => "Action.AiWorkflowUpdate",
            "自动排版" => "Action.AutoLayout", _ => null
        };
        if (exact is not null) return _localization.Text(exact);
        if (value.StartsWith("添加算子：", StringComparison.Ordinal))
        {
            var originalName = value["添加算子：".Length..];
            var descriptor = _registry.Descriptors.FirstOrDefault(item => item.DisplayName == originalName || item.EnglishName == originalName);
            return $"{_localization.Text("Action.AddOperator")}: {(descriptor is null ? "Operator" : _localization.OperatorName(descriptor))}";
        }
        if (value.StartsWith("修改参数：", StringComparison.Ordinal)) return _localization.Text("Action.ChangeParameter");
        return ContainsHan(value) ? _localization.Text("Action.Undo") : value;
    }

    private string UserError(Exception exception)
    {
        var message = exception.Message;
        return !_localization.IsChinese && ContainsHan(message)
            ? _localization.Format("Error.GenericOperation", exception.GetType().Name)
            : message;
    }

    private static bool ContainsHan(string? value) => value?.Any(character => character is >= '\u3400' and <= '\u9fff') == true;

    private void SyncThemeChecks()
    {
        DarkThemeMenuItem.IsChecked = WelcomeDarkThemeMenuItem.IsChecked = _themes.Current == UiThemeKind.Dark;
        LightThemeMenuItem.IsChecked = WelcomeLightThemeMenuItem.IsChecked = _themes.Current == UiThemeKind.Light;
        GradientThemeMenuItem.IsChecked = WelcomeGradientThemeMenuItem.IsChecked = _themes.Current == UiThemeKind.Gradient;
    }

    private static Avalonia.Media.IBrush ResourceBrush(string key) =>
        Application.Current?.Resources[key] as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.Transparent;

    private void EnterEditor()
    {
        foreach (var bitmap in _retiredPreviews) bitmap.Dispose();
        _retiredPreviews.Clear();
        WelcomeView.IsVisible = false;
        EditorView.IsVisible = true;
    }
    private void MarkDirty(bool notifyAutomation = true)
    {
        _dirty = true;
        if (notifyAutomation) _automation?.NotifyHostChanged();
        UpdateTitle();
        ScheduleAutosave();
    }

    private void AttachAutomationSession()
    {
        DetachAutomationSession();
        if (_project is null) return;
        _automation = new WorkflowAutomationSession(
            _project,
            _workflow,
            _projects,
            _registry,
            new OpenCvWorkflowAutomationRunner(_registry));
        _automation.WorkflowChanged += Automation_WorkflowChanged;
        _automation.ExecutionCompleted += Automation_ExecutionCompleted;
        AutomationHost.Attach(_automation);
    }

    private void DetachAutomationSession()
    {
        if (_automation is null) return;
        _automation.WorkflowChanged -= Automation_WorkflowChanged;
        _automation.ExecutionCompleted -= Automation_ExecutionCompleted;
        _automation = null;
        AutomationHost.Attach(null);
    }

    private void Automation_WorkflowChanged(object? sender, WorkflowChangedEventArgs change)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _undoStack.Push(new UndoEntry(WorkflowJson.Serialize(change.PreviousWorkflow), change.Summary));
            TrimUndoHistory();
            CancelPreview();
            _workflow = change.Workflow;
            _automation?.SynchronizeFromHost(_workflow, incrementRevision: false);
            Canvas.Workflow = _workflow;
            Canvas.Refresh();
            _dirty = false;
            UpdateTitle();
            if (!string.IsNullOrWhiteSpace(change.SelectedNodeId)) Canvas.SelectNodeById(change.SelectedNodeId);
            else ShowProperties(null);
            _ = LoadOperationHistoryAsync();
            Status(_localization.IsChinese ? $"AI 已更新工作流：{change.Summary}" : $"AI updated workflow: {change.Summary}");
        });
    }

    private void Automation_ExecutionCompleted(object? sender, WorkflowExecutionCompletedEventArgs completed)
    {
        Dispatcher.UIThread.Post(() => _ = ShowAutomationResultAsync(completed.Result));
    }

    private async Task ShowAutomationResultAsync(WorkflowAutomationRunResult result)
    {
        if (_project is null) return;
        if (!result.Success)
        {
            Status(_localization.IsChinese ? $"AI 工作流执行失败：{result.Error}" : $"AI workflow failed: {result.Error}");
            return;
        }

        var image = result.Outputs.FirstOrDefault(output => !string.IsNullOrWhiteSpace(output.ArtifactPath));
        if (image?.ArtifactPath is { } relative)
        {
            CancelPreview();
            var path = ProjectPaths.ResolveInside(_project.RootPath, relative);
            var bytes = await File.ReadAllBytesAsync(path);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            using var stream = new MemoryStream(bytes, writable: false);
            ReplacePreview(new Bitmap(stream), digest);
        }
        var summary = string.Join(_localization.IsChinese ? "；" : "; ", result.Outputs
            .Where(output => !string.IsNullOrWhiteSpace(output.Summary))
            .Select(output => $"{output.Port}={output.Summary}"));
        Status(_localization.IsChinese ? $"AI 工作流执行完成{(summary.Length == 0 ? "" : "：" + summary)}" : $"AI workflow completed{(summary.Length == 0 ? "" : ": " + summary)}");
    }

    private void ScheduleAutosave()
    {
        _autosaveCancellation?.Cancel();
        _autosaveCancellation?.Dispose();
        var cancellation = _autosaveCancellation = new CancellationTokenSource();
        _ = AutosaveAfterDelayAsync(cancellation.Token);
    }

    private async Task AutosaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(800, cancellationToken);
            if (_project is not null)
            {
                await _projects.SaveWorkflowAsync(_project, _workflow, createSnapshot: false, cancellationToken);
                _dirty = false;
                UpdateTitle();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status(_localization.Format("Status.AutosaveFailed", UserError(ex))); }
    }

    private async Task<string?> AskProjectNameAsync()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var input = new TextBox { Text = _localization.Format("Dialog.DefaultProjectName", stamp), PlaceholderText = _localization.Text("Dialog.ProjectNamePlaceholder") };
        var ok = new Button { Content = _localization.Text("Dialog.CreateProject"), Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = _localization.Text("Dialog.Cancel") };
        var dialog = new Window
        {
            Title = _localization.Text("Dialog.NewProjectTitle"), Width = 430, Height = 210, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = _localization.Text("Dialog.ProjectName"), FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = _localization.Text("Dialog.ProjectFolderHint"), Opacity = .65 }, input,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } }
                }
            }
        };
        ok.Click += (_, _) => dialog.Close(input.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<string?>(this);
    }

    private string DisplayName(WorkflowNode node) => _registry.TryGetDescriptor(node.Type, out var descriptor) ? _localization.OperatorName(descriptor) : node.Type;
    private static string PortTypeText(NodeValueType type) => type switch { NodeValueType.Mat => "图像矩阵", NodeValueType.Double => "小数", NodeValueType.Integer => "整数", NodeValueType.Boolean => "布尔值", NodeValueType.String => "文本", NodeValueType.Point => "点", NodeValueType.Points => "点集合", NodeValueType.Line => "直线", NodeValueType.Circle => "圆", NodeValueType.Rectangle => "矩形", NodeValueType.Contours => "轮廓集合", NodeValueType.Matrix => "变换矩阵", NodeValueType.Collection => "集合", _ => "任意数据" };
    private void UpdateTitle()
    {
        // The main window uses an integrated VS Code-style title bar. Keep the
        // native title text empty so it cannot overlap the left-aligned menus.
        Title = "";
        if (_project is not null)
            ProjectText.Text = $"{_project.Project.Name}{(_dirty ? " *" : "")}";
    }
    private void Status(string message) => StatusText.Text = message;
    private void WelcomeStatus(string message) => WelcomeStatusText.Text = message;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            UndoLastOperation();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachAutomationSession();
        _preview?.Dispose();
        foreach (var bitmap in _retiredPreviews) bitmap.Dispose();
        _retiredPreviews.Clear();
        _autosaveCancellation?.Cancel(); _autosaveCancellation?.Dispose();
        CancelPreview();
        base.OnClosed(e);
    }

    private sealed record UndoEntry(string WorkflowJson, string Description);
}
