using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.App;

/// <summary>
/// Runs the selected target node over a folder of images and writes a CSV report.
/// Images may live outside the project: each file is decoded and injected as the input
/// node's external output, so the workflow itself is never modified.
/// </summary>
public sealed class BatchRunWindow : Window {
  private sealed record NodeOption(string Id, string Label) {
    public override string ToString() => Label;
  }

  private readonly bool _chinese;
  private readonly ProjectHandle _project;
  private readonly WorkflowDocument _workflow;
  private readonly OperatorRegistry _registry;
  private readonly BatchWorkflowRunner _runner;
  private readonly ObservableCollection<string> _items = [];
  private readonly TextBox _folder = new() { IsReadOnly = true };
  private readonly ComboBox _inputNode = new();
  private readonly ComboBox _targetNode = new();
  private readonly TextBox _csvPath = new();
  private readonly CheckBox _recursive = new();
  private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6 };
  private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 11 };
  private readonly Button _start;
  private readonly Button _cancel;
  private readonly Button _openOutput;
  private CancellationTokenSource? _cancellation;
  private IReadOnlyList<string> _ports = [];

  public BatchRunWindow(ProjectHandle project, WorkflowDocument workflow, OperatorRegistry registry, string? selectedNodeId, bool chinese) {
    _chinese = chinese;
    _project = project;
    _workflow = workflow;
    _registry = registry;
    _runner = new BatchWorkflowRunner(registry, project.RootPath);
    Title = T("批量运行", "Batch run"); Width = 860; Height = 620; MinWidth = 760; MinHeight = 540;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    _start = Button(T("开始", "Start"), Start_Click);
    _cancel = Button(T("取消", "Cancel"), Cancel_Click);
    _openOutput = Button(T("打开输出目录", "Open output folder"), OpenOutput_Click);
    _cancel.IsEnabled = false;
    _openOutput.IsEnabled = false;
    Content = BuildContent(registry, selectedNodeId);
  }

  private Control BuildContent(OperatorRegistry registry, string? selectedNodeId) {
    var root = new Grid {
      Margin = new Thickness(14),
      RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*,Auto"),
      RowSpacing = 10
    };

    var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,Auto,Auto"), ColumnSpacing = 8 };
    folderRow.Children.Add(Label(T("源文件夹", "Source folder")));
    Grid.SetColumn(_folder, 1); folderRow.Children.Add(_folder);
    var browse = Button(T("选择…", "Browse…"), BrowseFolder_Click); Grid.SetColumn(browse, 2); folderRow.Children.Add(browse);
    _recursive.Content = T("含子文件夹", "Include subfolders");
    Grid.SetColumn(_recursive, 3); folderRow.Children.Add(_recursive);
    root.Children.Add(folderRow);

    var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), ColumnSpacing = 8 };
    inputRow.Children.Add(Label(T("输入节点", "Input node")));
    Grid.SetColumn(_inputNode, 1); inputRow.Children.Add(_inputNode);
    Grid.SetRow(inputRow, 1); root.Children.Add(inputRow);

    var targetRow = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), ColumnSpacing = 8 };
    targetRow.Children.Add(Label(T("目标节点", "Target node")));
    Grid.SetColumn(_targetNode, 1); targetRow.Children.Add(_targetNode);
    Grid.SetRow(targetRow, 2); root.Children.Add(targetRow);

    var csvRow = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), ColumnSpacing = 8 };
    csvRow.Children.Add(Label(T("CSV 输出", "CSV output")));
    Grid.SetColumn(_csvPath, 1); csvRow.Children.Add(_csvPath);
    Grid.SetRow(csvRow, 3); root.Children.Add(csvRow);

    Grid.SetRow(_progress, 4); root.Children.Add(_progress);
    Grid.SetRow(_status, 5); root.Children.Add(_status);

    var list = new ListBox { ItemsSource = _items, FontSize = 11 };
    Grid.SetRow(list, 6); root.Children.Add(list);

    var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
    footer.Children.Add(_openOutput);
    footer.Children.Add(_start);
    footer.Children.Add(_cancel);
    var close = Button(T("关闭", "Close"), (_, _) => Close());
    footer.Children.Add(close);
    Grid.SetRow(footer, 7); root.Children.Add(footer);

    Populate(registry, selectedNodeId);
    return root;
  }

  private void Populate(OperatorRegistry registry, string? selectedNodeId) {
    string defaultFolder = Path.Combine(_project.RootPath, "images", "input");
    _folder.Text = Directory.Exists(defaultFolder) ? defaultFolder : _project.RootPath;
    _csvPath.Text = $"output/batch-{DateTime.Now:yyyyMMdd-HHmmss}.csv";

    var inputs = _workflow.Nodes
        .Where(node => node.Type == "Image.Load")
        .Select(node => new NodeOption(node.Id, $"{node.Name} [{node.Id}]"))
        .ToList();
    _inputNode.ItemsSource = inputs;
    _inputNode.SelectedItem = inputs.FirstOrDefault();

    var targets = _workflow.Nodes
        .Select(node => (node, descriptor: registry.TryGetDescriptor(node.Type, out var found) ? found : null))
        .Where(item => item.descriptor is not null && item.descriptor.Outputs.Count > 0)
        .Select(item => new NodeOption(item.node.Id, $"{item.node.Name} [{item.node.Id}]"))
        .ToList();
    _targetNode.ItemsSource = targets;
    _targetNode.SelectedItem = targets.FirstOrDefault(option => option.Id == selectedNodeId) ?? targets.LastOrDefault();

    _status.Text = T("选择源文件夹后点“开始”。图片不需要放进项目目录。",
        "Pick a source folder and press Start. Images do not need to live inside the project.");
  }

  private async void BrowseFolder_Click(object? sender, RoutedEventArgs e) {
    var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = T("选择图片文件夹", "Select image folder"), AllowMultiple = false });
    string? path = folders.FirstOrDefault()?.TryGetLocalPath();
    if (!string.IsNullOrWhiteSpace(path)) {
      _folder.Text = path;
    }
  }

  private async void Start_Click(object? sender, RoutedEventArgs e) {
    if (_inputNode.SelectedItem is not NodeOption input || _targetNode.SelectedItem is not NodeOption target) {
      _status.Text = T("请先选择输入节点和目标节点。", "Select an input and a target node first.");
      return;
    }

    if (!Directory.Exists(_folder.Text)) {
      _status.Text = T("源文件夹不存在。", "Source folder does not exist.");
      return;
    }

    if (!IsUpstream(input.Id, target.Id)) {
      _status.Text = T(
          "输入节点不是目标节点的上游，批量结果会与所选图片无关。请检查连线，或改选正确的输入节点。",
          "The input node is not upstream of the target node, so each run would ignore the folder image. Check the wiring or pick the right input node.");
      return;
    }

    var files = BatchWorkflowRunner.CollectImages(_folder.Text!, _recursive.IsChecked == true);
    if (files.Count == 0) {
      _status.Text = T("该文件夹里没有可识别的图片（png/jpg/bmp/tif/webp）。", "No supported images (png/jpg/bmp/tif/webp) found in that folder.");
      return;
    }

    _items.Clear();
    _progress.Maximum = files.Count;
    _progress.Value = 0;
    _start.IsEnabled = false;
    _cancel.IsEnabled = true;
    _openOutput.IsEnabled = false;
    var cancellation = _cancellation = new CancellationTokenSource();
    var workflow = WorkflowJson.Deserialize(WorkflowJson.Serialize(_workflow));
    var progress = new Progress<BatchItemResult>(item => {
      _progress.Value += 1;
      _items.Add(Describe(item));
      _status.Text = T($"已处理 {_items.Count}/{files.Count}", $"Processed {_items.Count}/{files.Count}");
    });

    try {
      var results = await Task.Run(
          () => _runner.RunAsync(workflow, input.Id, target.Id, files, progress, cancellation.Token),
          cancellation.Token);
      string csvPath = ResolveCsvPath();
      _ports = DescribePorts(target.Id);
      await BatchResultCsv.WriteAsync(csvPath, _ports, results, CancellationToken.None);
      int failed = results.Count(item => !item.Success);
      _status.Text = T(
          $"完成：成功 {results.Count - failed} / 失败 {failed}，用时 {results.Sum(item => item.Duration.TotalSeconds):F1}s\nCSV：{csvPath}",
          $"Done: {results.Count - failed} succeeded, {failed} failed in {results.Sum(item => item.Duration.TotalSeconds):F1}s\nCSV: {csvPath}");
      _openOutput.IsEnabled = true;
    } catch (OperationCanceledException) {
      _status.Text = T("已取消。已完成的条目不会写入 CSV。", "Cancelled. Completed rows were not written to the CSV.");
    } catch (Exception exception) {
      _status.Text = T("运行失败：", "Run failed: ") + exception.Message;
    } finally {
      _cancellation = null;
      cancellation.Dispose();
      _start.IsEnabled = true;
      _cancel.IsEnabled = false;
    }
  }

  private void Cancel_Click(object? sender, RoutedEventArgs e) {
    _cancellation?.Cancel();
  }

  private void OpenOutput_Click(object? sender, RoutedEventArgs e) {
    string directory = Path.GetDirectoryName(ResolveCsvPath()) ?? _project.RootPath;
    try {
      Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    } catch (Exception exception) {
      _status.Text = exception.Message;
    }
  }

  private string ResolveCsvPath() {
    string relative = string.IsNullOrWhiteSpace(_csvPath.Text) ? "output/batch.csv" : _csvPath.Text.Trim();
    return Path.IsPathRooted(relative) ? relative : ProjectPaths.ResolveInside(_project.RootPath, relative);
  }

  private bool IsUpstream(string candidate, string targetNodeId) {
    var seen = new HashSet<string>(StringComparer.Ordinal) { targetNodeId };
    var pending = new Queue<string>([targetNodeId]);
    while (pending.Count > 0) {
      string current = pending.Dequeue();
      foreach (var connection in _workflow.Connections.Where(item => item.ToNode == current)) {
        if (connection.FromNode == candidate) {
          return true;
        }

        if (seen.Add(connection.FromNode)) {
          pending.Enqueue(connection.FromNode);
        }
      }
    }

    return false;
  }

  private IReadOnlyList<string> DescribePorts(string targetNodeId) {
    var node = _workflow.Nodes.FirstOrDefault(item => item.Id == targetNodeId);
    return node is null || !_registry.TryGetDescriptor(node.Type, out var descriptor)
        ? []
        : [.. descriptor.Outputs.Select(port => port.Name)];
  }

  private string Describe(BatchItemResult item) {
    string head = item.Success ? "✔" : "✖";
    string duration = $"{item.Duration.TotalMilliseconds:F0} ms";
    if (!item.Success) {
      return $"{head} {item.File} · {duration} · {item.Error}";
    }

    string values = string.Join(", ", item.Outputs.Where(output => output.Summary.Length > 0).Select(output => $"{output.Port}={output.Summary}"));
    return values.Length == 0 ? $"{head} {item.File} · {duration}" : $"{head} {item.File} · {duration} · {values}";
  }

  private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
  private static Button Button(string text, EventHandler<RoutedEventArgs> click) { var button = new Button { Content = text }; button.Click += click; return button; }
  private string T(string chinese, string english) => _chinese ? chinese : english;
}
