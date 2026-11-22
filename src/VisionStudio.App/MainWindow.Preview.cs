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
  private void PreviewFit_Click(object? sender, RoutedEventArgs e) {
    _previewFit = true;
    ApplyPreviewScale();
  }

  private void PreviewZoomIn_Click(object? sender, RoutedEventArgs e) {
    SetPreviewZoom(_previewZoom * 1.2);
  }

  private void PreviewZoomOut_Click(object? sender, RoutedEventArgs e) {
    SetPreviewZoom(_previewZoom / 1.2);
  }

  private void Preview_PointerWheelChanged(object? sender, PointerWheelEventArgs e) {
    if (_preview is null) {
      return;
    }

    SetPreviewZoom(_previewZoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15));
    e.Handled = true;
  }

  private void ShowProperties(WorkflowNode? node) {
    PropertyPanel.Children.Clear();
    if (node is null || !_registry.TryGetDescriptor(node.Type, out var descriptor)) {
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
    string operatorDescription = _localization.OperatorDescription(descriptor);
    if (!string.IsNullOrWhiteSpace(operatorDescription)) {
      identity.Children.Add(new TextBlock { Text = operatorDescription, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .78, Margin = new Thickness(0, 3, 0, 0) });
    }

    PropertyPanel.Children.Add(new Border {
      Background = ResourceBrush("InspectorCardBrush"),
      BorderBrush = ResourceBrush("BorderBrush"),
      BorderThickness = new Thickness(1),
      CornerRadius = new CornerRadius(4),
      Padding = new Thickness(11),
      Child = identity,
      Margin = new Thickness(0, 0, 0, 6)
    });

    foreach (var parameter in descriptor.Parameters) {
      string parameterName = _localization.IsChinese ? parameter.DisplayName : parameter.Name;
      PropertyPanel.Children.Add(new TextBlock { Text = parameterName, Margin = new Thickness(0, 8, 0, 0), FontWeight = Avalonia.Media.FontWeight.SemiBold });
      if (_localization.IsChinese && !string.IsNullOrWhiteSpace(parameter.Description)) {
        PropertyPanel.Children.Add(new TextBlock { Text = parameter.Description, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .62, FontSize = 12 });
      }

      string current = node.Parameters.TryGetValue(parameter.Name, out var element) ? element.ToString() : parameter.DefaultValue.ToString() ?? "";
      var editor = new TextBox { Text = current, Tag = parameter };
      AutomationProperties.SetName(editor, parameterName);
      editor.LostFocus += (_, _) => ApplyParameter(node, editor);
      PropertyPanel.Children.Add(editor);
      if (node.Type == "Image.Load" && parameter.Name == "path") {
        var browse = new Button { Content = _localization.Text("Property.ImportImage"), HorizontalAlignment = HorizontalAlignment.Stretch };
        browse.Click += async (_, _) => await ChooseInputImageAsync(node);
        PropertyPanel.Children.Add(browse);
      }
    }

    if (descriptor.Inputs.Count > 0) {
      PropertyPanel.Children.Add(new TextBlock { Text = _localization.Text("Property.Inputs"), FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
      foreach (var port in descriptor.Inputs) {
        PropertyPanel.Children.Add(new TextBlock { Text = $"● {(_localization.IsChinese ? port.DisplayName ?? port.Name : port.Name)}：{(_localization.IsChinese ? port.Description ?? PortTypeText(port.Type) : port.Type.ToString())}", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .7 });
      }
    }
    if (descriptor.Outputs.Count > 0) {
      PropertyPanel.Children.Add(new TextBlock { Text = _localization.Text("Property.Outputs"), FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
      foreach (var port in descriptor.Outputs) {
        PropertyPanel.Children.Add(new TextBlock { Text = $"● {(_localization.IsChinese ? port.DisplayName ?? port.Name : port.Name)}：{(_localization.IsChinese ? port.Description ?? PortTypeText(port.Type) : port.Type.ToString())}", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .7 });
      }
    }
  }

  private async Task ChooseInputImageAsync(WorkflowNode node) {
    if (_project is null) {
      return;
    }

    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = _localization.Text("Picker.ImportImage"),
      AllowMultiple = false,
      FileTypeFilter = [new FilePickerFileType(_localization.Text("Picker.ImageFiles")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff", "*.webp"] }]
    });
    string? source = files.FirstOrDefault()?.TryGetLocalPath();
    if (source is null) {
      return;
    }

    try {
      string relative = await _projects.ImportInputImageAsync(_project, source);
      RecordUndo("修改输入图片");
      node.SetParameter("path", relative);
      MarkDirty();
      ShowProperties(node);
      SchedulePreview(node);
      Status(_localization.Format("Status.ImageImported", relative));
    } catch (Exception ex) { Status(UserError(ex)); }
  }

  private void ApplyParameter(WorkflowNode node, TextBox editor) {
    if (editor.Tag is not ParameterDefinition parameter) {
      return;
    }

    try {
      object value = parameter.Kind switch {
        JsonValueKind.Number when parameter.DefaultValue is int => int.Parse(editor.Text ?? "", CultureInfo.InvariantCulture),
        JsonValueKind.Number => double.Parse(editor.Text ?? "", CultureInfo.InvariantCulture),
        JsonValueKind.True or JsonValueKind.False => bool.Parse(editor.Text ?? ""),
        _ => editor.Text ?? ""
      };
      if (value is IConvertible convertible && parameter.Kind == JsonValueKind.Number) {
        double number = convertible.ToDouble(CultureInfo.InvariantCulture);
        if (parameter.Minimum is { } min && number < min || parameter.Maximum is { } max && number > max) {
          throw new ArgumentOutOfRangeException(parameter.Name);
        }
      }
      var serialized = JsonSerializer.SerializeToElement(value, WorkflowJson.Options);
      if (node.Parameters.TryGetValue(parameter.Name, out var existing) && existing.GetRawText() == serialized.GetRawText()) {
        return;
      }

      RecordUndo($"修改参数：{parameter.DisplayName}");
      node.SetParameter(parameter.Name, value);
      MarkDirty();
      SchedulePreview(node);
    } catch (Exception ex) { Status(_localization.Format("Status.InvalidParameter", UserError(ex))); ShowProperties(node); }
  }

  private void SchedulePreview(WorkflowNode? node) {
    CancelPreview();
    if (node is null || _project is null) {
      return;
    }

    var cancellation = _previewCancellation = new CancellationTokenSource();
    _ = PreviewAfterDelayAsync(node, cancellation.Token);
  }

  private void CancelPreview() {
    var cancellation = _previewCancellation;
    _previewCancellation = null;
    if (cancellation is null) {
      return;
    }

    cancellation.Cancel();
    cancellation.Dispose();
  }

  private async Task PreviewAfterDelayAsync(WorkflowNode node, CancellationToken cancellationToken) {
    try {
      await Task.Delay(120, cancellationToken);
      Status(_localization.Format("Status.PreviewLoading", DisplayName(node)));

      // Operators are CPU-bound native OpenCV calls. Running them on the dispatcher froze the
      // whole window (see the HoughCircles incident), so the slice executes on the thread pool
      // with a snapshot workflow and per-node progress.
      var workflow = SnapshotWorkflow();
      string rootPath = _project!.RootPath;
      BeginRun(HasSlowOperator(workflow, node.Id));
      var executor = new WorkflowExecutor(_registry, rootPath, new OpenCvProjectCache(rootPath)) {
        Progress = new Progress<WorkflowProgress>(item => ReportRunProgress(item))
      };
      using var result = await Task.Run(() => executor.ExecuteAsync(workflow, node.Id, cancellationToken), cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      await RecordExecutionAsync(node, result);
      var descriptor = _registry.Descriptors.Single(x => x.Type == node.Type);
      var imagePort = descriptor.Outputs.FirstOrDefault(x => x.Type == NodeValueType.Mat);
      if (imagePort is null || !result.TryGetOutput<Mat>(node.Id, imagePort.Name, out var mat) || mat is null) {
        string values = DescribeOutputs(result.GetNodeOutputs(node.Id));
        Status(values.Length == 0 ? _localization.Text("Status.NoPreviewOutput") : _localization.Format("Status.ResultValues", _localization.OperatorName(descriptor), values));
        return;
      }
      string previewPath = ProjectPaths.ResolveInside(_project.RootPath, ".project/autosave/preview.png");
      OpenCvFileCodec.Write(previewPath, mat);
      byte[] previewBytes = await File.ReadAllBytesAsync(previewPath, cancellationToken);
      string digest = Convert.ToHexString(SHA256.HashData(previewBytes));
      if (string.Equals(digest, _previewDigest, StringComparison.Ordinal)) {
        int cachedUnchanged = result.Timings.Count(x => x.FromCache);
        Status(_localization.Format("Status.ResultUnchanged", _localization.OperatorName(descriptor), cachedUnchanged, result.Timings.Count));
        return;
      }
      using var stream = new MemoryStream(previewBytes, writable: false);
      var bitmap = new Bitmap(stream);
      if (cancellationToken.IsCancellationRequested) {
        bitmap.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
      }
      ReplacePreview(bitmap, digest);
      int cached = result.Timings.Count(x => x.FromCache);
      double elapsed = result.Timings.Sum(x => x.Duration.TotalMilliseconds);
      string summary = DescribeOutputs(result.GetNodeOutputs(node.Id));
      string outputSummary = summary.Length == 0 ? "" : _localization.Format("Status.OutputSummary", summary);
      Status(_localization.Format("Status.ResultShown", _localization.OperatorName(descriptor), outputSummary, cached, result.Timings.Count, elapsed));
    } catch (OperationCanceledException) { Status(_localization.Text("Status.Cancelled")); } catch (Exception ex) { Status(_localization.Format("Status.PreviewFailed", UserError(ex))); } finally { EndRun(); }
  }

  /// <summary>Deep copy so the thread-pool execution cannot observe edits made while it runs.</summary>
  private async Task RecordExecutionAsync(WorkflowNode node, WorkflowExecutionResult result) {
    var history = _executionHistory;
    if (history is null) {
      return;
    }

    try {
      var outputs = result.GetNodeOutputs(node.Id)
          .ToDictionary(pair => pair.Key, pair => ExecutionHistoryStore.Summarize(pair.Value), StringComparer.Ordinal);
      await history.AddAsync(ExecutionHistoryStore.Create(
          node, outputs, result.Timings.Sum(timing => timing.Duration.TotalMilliseconds)));
    } catch (Exception) {
      // History is diagnostic only.
    }
  }

  private WorkflowDocument SnapshotWorkflow() {
    return WorkflowJson.Deserialize(WorkflowJson.Serialize(_workflow));
  }

  private static readonly string[] SlowOperatorPrefixes = [
      "LineCircle.Hough", "Locate.SIFT", "Locate.ORB", "Locate.FeatureMatch", "Locate.TemplateMatch",
      "Locate.MultiTemplate", "Locate.FlannMatcher", "Locate.BFMatcher", "Transform.DFT"
  ];

  private static bool HasSlowOperator(WorkflowDocument workflow, string targetNodeId) {
    var types = workflow.Nodes.ToDictionary(node => node.Id, node => node.Type, StringComparer.Ordinal);
    var slice = new HashSet<string>(StringComparer.Ordinal) { targetNodeId };
    bool added = true;
    while (added) {
      added = false;
      foreach (var connection in workflow.Connections) {
        if (slice.Contains(connection.ToNode) && slice.Add(connection.FromNode)) {
          added = true;
        }
      }
    }

    return slice.Any(id => types.TryGetValue(id, out string? type) &&
        SlowOperatorPrefixes.Any(prefix => type.StartsWith(prefix, StringComparison.Ordinal)));
  }

  private void BeginRun(bool slow) {
    _runStarted = DateTime.UtcNow;
    _runIndex = 0;
    _runTotal = 0;
    _runNodeLabel = null;
    if (CancelRunButton is not null) {
      CancelRunButton.IsVisible = true;
    }

    if (slow) {
      Status(_localization.Text("Status.SlowOperator"));
    }

    _runTimer.Start();
  }

  private void EndRun() {
    _runTimer.Stop();
    if (CancelRunButton is not null) {
      CancelRunButton.IsVisible = false;
    }
  }

  private void ReportRunProgress(WorkflowProgress progress) {
    _runIndex = progress.Index;
    _runTotal = progress.Total;
    _runNodeLabel = _registry.TryGetDescriptor(progress.NodeType, out var descriptor)
        ? _localization.OperatorName(descriptor)
        : progress.NodeType;
  }

  private void RefreshRunStatus() {
    if (_runTotal == 0 || _runNodeLabel is null) {
      double waited = RunElapsed;
      Status(_localization.Format("Status.RunningIdle", waited));
      return;
    }

    Status(_localization.Format("Status.Running", _runIndex, _runTotal, _runNodeLabel, RunElapsed));
  }

  private double RunElapsed => (DateTime.UtcNow - _runStarted).TotalSeconds;

  private void CancelRun_Click(object? sender, RoutedEventArgs e) {
    CancelPreview();
    EndRun();
    Status(_localization.Text("Status.Cancelled"));
  }

  private void ReplacePreview(Bitmap bitmap, string digest) {
    var previous = _preview;
    _preview = bitmap;
    _previewDigest = digest;
    PreviewImage.Source = bitmap;
    PreviewEmptyState.IsVisible = false;
    _previewFit = true;
    ApplyPreviewScale();
    // Keep replaced frames alive while the editor is visible. Avalonia's render
    // thread can retain an older frame beyond the UI dispatcher turn.
    if (previous is not null) {
      _retiredPreviews.Add(previous);
    }
  }

  private void SetPreviewZoom(double zoom) {
    _previewFit = false;
    _previewZoom = Math.Clamp(zoom, .05, 16);
    ApplyPreviewScale();
  }

  private void ApplyPreviewScale() {
    if (_preview is null) {
      return;
    }

    if (_previewFit) {
      double availableWidth = Math.Max(1, PreviewScroll.Bounds.Width - 6);
      double availableHeight = Math.Max(1, PreviewScroll.Bounds.Height - 6);
      _previewZoom = Math.Clamp(Math.Min(availableWidth / _preview.PixelSize.Width, availableHeight / _preview.PixelSize.Height), .05, 16);
    }
    PreviewImage.Width = Math.Max(1, _preview.PixelSize.Width * _previewZoom);
    PreviewImage.Height = Math.Max(1, _preview.PixelSize.Height * _previewZoom);
  }

  private string DescribeOutputs(IReadOnlyDictionary<string, object> outputs) {
    return string.Join(_localization.IsChinese ? "；" : "; ", outputs
      .Where(pair => pair.Value is not Mat)
      .Select(pair => $"{pair.Key}={pair.Value switch { System.Collections.ICollection list => _localization.Format("Value.Collection", list.Count), _ => pair.Value }}")
      .Take(6));
  }

  private void ReleasePreviewsAfterNavigation() {
    PreviewImage.Source = null;
    PreviewEmptyState.IsVisible = true;
    if (_preview is not null) {
      _retiredPreviews.Add(_preview);
    }

    _preview = null;
    _previewDigest = null;
    PreviewImage.Width = double.NaN;
    PreviewImage.Height = double.NaN;
  }

  private string DisplayName(WorkflowNode node) {
    return _registry.TryGetDescriptor(node.Type, out var descriptor) ? _localization.OperatorName(descriptor) : node.Type;
  }

  private static string PortTypeText(NodeValueType type) {
    return type switch { NodeValueType.Mat => "图像矩阵", NodeValueType.Double => "小数", NodeValueType.Integer => "整数", NodeValueType.Boolean => "布尔值", NodeValueType.String => "文本", NodeValueType.Point => "点", NodeValueType.Points => "点集合", NodeValueType.Line => "直线", NodeValueType.Circle => "圆", NodeValueType.Rectangle => "矩形", NodeValueType.Contours => "轮廓集合", NodeValueType.Matrix => "变换矩阵", NodeValueType.Collection => "集合", _ => "任意数据" };
  }

}
