using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VisionStudio.Core;

namespace VisionStudio.App;

public sealed partial class GraphCanvas : Control {
  private IBrush _canvasBrush = Solid("#1E1E1E");
  private IBrush _nodeBrush = Solid("#252526");
  private IBrush _selectedNodeBrush = Solid("#2A2D35");
  private IBrush _nodeBorderBrush = Solid("#454545");
  private IBrush _selectedBorderBrush = Solid("#007ACC");
  private IBrush _headerBrush = Solid("#333337");
  private IBrush _selectedHeaderBrush = Solid("#094771");
  private IBrush _nodeTextBrush = Solid("#CCCCCC");
  private IBrush _titleTextBrush = Brushes.White;
  private IBrush _portOutlineBrush = Solid("#D4D4D4");
  private IBrush _minorGridBrush = Solid("#252526");
  private IBrush _majorGridBrush = Solid("#303033");
  private IBrush _shadowBrush = Solid("#70000000");
  private static readonly IBrush ConnectionBrush = new SolidColorBrush(Color.Parse("#4FC1FF"));
  private static readonly IBrush ConnectionHoverBrush = new SolidColorBrush(Color.Parse("#9CDCFE"));
  private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#FFCC66"));
  private static readonly IBrush InputPortBrush = new SolidColorBrush(Color.Parse("#6A9955"));
  private static readonly IBrush OutputPortBrush = new SolidColorBrush(Color.Parse("#4FC1FF"));
  private readonly Dictionary<string, Rect> _bounds = new(StringComparer.Ordinal);
  private WorkflowNode? _dragged;
  private bool _dragUndoRecorded;
  private Point _dragOffset;
  private (WorkflowNode Node, PortDefinition Port, int Index)? _dragConnection;
  private (WorkflowNode Node, PortDefinition Port, int Index)? _hoveredInput;
  private Point _connectionPointer;
  private WorkflowConnection? _hoveredConnection;
  private readonly HashSet<string> _selectedNodeIds = new(StringComparer.Ordinal);
  private bool _panning;
  private Point _panStart;
  private Vector _panOrigin;

  public WorkflowDocument? Workflow { get; set; }

  public void ApplyTheme(UiThemeKind theme) {
    if (theme == UiThemeKind.Light) {
      _canvasBrush = Solid("#FAFAFA"); _nodeBrush = Solid("#FFFFFF"); _selectedNodeBrush = Solid("#EAF4FF");
      _nodeBorderBrush = Solid("#B8B8B8"); _selectedBorderBrush = Solid("#0067B8"); _headerBrush = Solid("#E8E8EA");
      _selectedHeaderBrush = Solid("#CCE8FF"); _nodeTextBrush = Solid("#333333"); _titleTextBrush = Solid("#1F1F1F");
      _portOutlineBrush = Solid("#555555"); _minorGridBrush = Solid("#EEEEEE"); _majorGridBrush = Solid("#D8D8D8"); _shadowBrush = Solid("#26000000");
    } else if (theme == UiThemeKind.Gradient) {
      _canvasBrush = Gradient("#171927", "#202A42"); _nodeBrush = Solid("#252B3D"); _selectedNodeBrush = Solid("#303852");
      _nodeBorderBrush = Solid("#4A5573"); _selectedBorderBrush = Solid("#817AFF"); _headerBrush = Solid("#333B52");
      _selectedHeaderBrush = Solid("#5147A8"); _nodeTextBrush = Solid("#E2E5F2"); _titleTextBrush = Solid("#F1F3FF");
      _portOutlineBrush = Solid("#D8DCF0"); _minorGridBrush = Solid("#252B3D"); _majorGridBrush = Solid("#35405D"); _shadowBrush = Solid("#80000000");
    } else {
      _canvasBrush = Solid("#1E1E1E"); _nodeBrush = Solid("#252526"); _selectedNodeBrush = Solid("#2A2D35");
      _nodeBorderBrush = Solid("#454545"); _selectedBorderBrush = Solid("#007ACC"); _headerBrush = Solid("#333337");
      _selectedHeaderBrush = Solid("#094771"); _nodeTextBrush = Solid("#CCCCCC"); _titleTextBrush = Brushes.White;
      _portOutlineBrush = Solid("#D4D4D4"); _minorGridBrush = Solid("#252526"); _majorGridBrush = Solid("#303033"); _shadowBrush = Solid("#70000000");
    }
    InvalidateVisual();
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    context.FillRectangle(_canvasBrush, Bounds);
    if (Workflow is null || Registry is null) {
      return;
    }

    DrawGrid(context);
    _bounds.Clear();
    using var transform = context.PushTransform(Matrix.CreateTranslation(PanOffset.X, PanOffset.Y));
    foreach (var edge in Workflow.Connections) {
      if (!TryGetConnectionPoints(edge, out var from, out var to)) {
        continue;
      }

      bool selected = Equals(edge, SelectedConnection);
      bool hovered = Equals(edge, _hoveredConnection);
      DrawConnection(context, from, to, selected ? SelectionBrush : hovered ? ConnectionHoverBrush : ConnectionBrush, selected ? 3.5 : hovered ? 2.8 : 1.8);
    }
    if (_dragConnection is { } pending) {
      DrawConnection(context, OutputPoint(pending.Node, pending.Index), _connectionPointer, SelectionBrush, 2.5);
    }

    foreach (var node in Workflow.Nodes) {
      DrawNode(context, node);
    }
  }

  private void DrawNode(DrawingContext context, WorkflowNode node) {
    if (Registry is null || !Registry.TryGetDescriptor(node.Type, out var descriptor)) {
      return;
    }

    int rows = Math.Max(descriptor.Inputs.Count, descriptor.Outputs.Count);
    double height = HeaderHeight + 18 + Math.Max(1, rows) * PortSpacing;
    var rect = new Rect(node.X, node.Y, NodeWidth, height);
    _bounds[node.Id] = rect;
    context.DrawRectangle(_shadowBrush, null, rect.Translate(new Vector(2, 3)), 5, 5);
    bool selected = SelectedNode == node || _selectedNodeIds.Contains(node.Id);
    context.DrawRectangle(selected ? _selectedNodeBrush : _nodeBrush, new Pen(selected ? _selectedBorderBrush : _nodeBorderBrush, selected ? 2 : 1), rect, 5, 5);
    context.DrawRectangle(selected ? _selectedHeaderBrush : _headerBrush, null, new Rect(rect.X, rect.Y, rect.Width, HeaderHeight), 5, 5);
    context.DrawRectangle(CategoryBrush(descriptor.Category), null, new Rect(rect.X, rect.Y + 4, 3, HeaderHeight - 8));
    DrawText(context, OperatorName(descriptor), new Point(rect.X + 12, rect.Y + 10), 13, _titleTextBrush);
    string? reference = NodeReferenceSelector?.Invoke(node);
    if (!string.IsNullOrWhiteSpace(reference)) {
      var badge = ReferenceBadgeRect(node);
      context.DrawRectangle(CategoryBrush(descriptor.Category), null, badge, 3, 3);
      DrawText(context, reference, new Point(badge.X + 5, badge.Y + 4), 9, Brushes.White);
    }
    for (int i = 0; i < descriptor.Inputs.Count; i++) {
      var port = descriptor.Inputs[i]; var p = InputPoint(node, i);
      bool active = _hoveredInput?.Node == node && _hoveredInput?.Index == i;
      context.DrawEllipse(active ? SelectionBrush : InputPortBrush, new Pen(_portOutlineBrush, active ? 2 : 1), p, active ? 8 : 6, active ? 8 : 6);
      DrawText(context, PortName(port), p + new Vector(11, -7), 11, _nodeTextBrush);
    }
    for (int i = 0; i < descriptor.Outputs.Count; i++) {
      var port = descriptor.Outputs[i]; var p = OutputPoint(node, i);
      bool active = _dragConnection?.Node == node && _dragConnection?.Index == i;
      context.DrawEllipse(active ? SelectionBrush : OutputPortBrush, new Pen(_portOutlineBrush, 1), p, 6, 6);
      string text = PortName(port);
      DrawText(context, text, p + new Vector(-12 - text.Length * 11, -7), 11, _nodeTextBrush);
    }
  }

  private static Rect ReferenceBadgeRect(WorkflowNode node) {
    return new(node.X + NodeWidth - 62, node.Y + 7, 55, 22);
  }

  private void DrawGrid(DrawingContext context) {
    var minor = new Pen(_minorGridBrush, 1);
    var major = new Pen(_majorGridBrush, 1);
    double startX = PositiveModulo(PanOffset.X, GridSize);
    double startY = PositiveModulo(PanOffset.Y, GridSize);
    for (double x = startX; x < Bounds.Width; x += GridSize) {
      int worldIndex = (int)Math.Round((x - PanOffset.X) / GridSize);
      context.DrawLine(worldIndex % 5 == 0 ? major : minor, new Point(x, 0), new Point(x, Bounds.Height));
    }
    for (double y = startY; y < Bounds.Height; y += GridSize) {
      int worldIndex = (int)Math.Round((y - PanOffset.Y) / GridSize);
      context.DrawLine(worldIndex % 5 == 0 ? major : minor, new Point(0, y), new Point(Bounds.Width, y));
    }
  }

  private static void DrawConnection(DrawingContext context, Point a, Point b, IBrush brush, double width) {
    double midX = b.X > a.X + 80 ? (a.X + b.X) / 2 : a.X + 60;
    int verticalDirection = Math.Sign(b.Y - a.Y);
    int finalDirection = Math.Sign(b.X - midX);
    double radius = Math.Min(12, Math.Abs(b.Y - a.Y) / 2);
    var geometry = new StreamGeometry();
    using (var path = geometry.Open()) {
      path.BeginFigure(a, false);
      if (radius < 1) {
        path.LineTo(new(midX, a.Y));
        path.LineTo(b);
      } else {
        path.LineTo(new(midX - 12, a.Y));
        path.QuadraticBezierTo(new(midX, a.Y), new(midX, a.Y + verticalDirection * radius));
        path.LineTo(new(midX, b.Y - verticalDirection * radius));
        path.QuadraticBezierTo(new(midX, b.Y), new(midX + finalDirection * 12, b.Y));
        path.LineTo(b);
      }
    }
    context.DrawGeometry(null, new Pen(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
  }

  private string OperatorName(OperatorDescriptor descriptor) {
    return DisplayNameSelector?.Invoke(descriptor) ?? descriptor.DisplayName;
  }

  private string PortName(PortDefinition port) {
    return PortNameSelector?.Invoke(port) ?? port.DisplayName ?? port.Name;
  }

  private static IBrush CategoryBrush(string category) {
    string color = category switch {
      "输入" => "#4EC9B0",
      "图像" or "基础处理" => "#569CD6",
      "滤波" or "增强" => "#C586C0",
      "阈值" or "二值处理" or "分割" => "#DCDCAA",
      "形态学" or "边缘" => "#CE9178",
      "轮廓" or "Blob" or "测量" => "#B5CEA8",
      "逻辑" or "集合" => "#9CDCFE",
      "AI" => "#C586C0",
      _ => "#808080"
    };
    return new SolidColorBrush(Color.Parse(color));
  }

  private static void DrawText(DrawingContext context, string text, Point origin, double size, IBrush brush) {
    context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush), origin);
  }

  private static IBrush Solid(string color) {
    return new SolidColorBrush(Color.Parse(color));
  }

  private static IBrush Gradient(string start, string end) {
    return new LinearGradientBrush {
      StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
      EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
      GradientStops = [new(Color.Parse(start), 0), new(Color.Parse(end), 1)]
    };
  }

}
