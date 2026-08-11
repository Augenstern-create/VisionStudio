using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Input;
using Avalonia.Media;
using VisionStudio.Core;

namespace VisionStudio.App;

public sealed record WorkflowCanvasChangedEventArgs(bool AffectsExecution);
public sealed record WorkflowCanvasChangingEventArgs(string Description, bool AffectsExecution);

public sealed class GraphCanvas : Control
{
    private const double NodeWidth = 184;
    private const double HeaderHeight = 36;
    private const double PortSpacing = 28;
    private const double PortHitRadius = 19;
    private const double GridSize = 32;
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
    private bool _panning;
    private Point _panStart;
    private Vector _panOrigin;
    private Vector _panOffset;

    public WorkflowDocument? Workflow { get; set; }
    public OperatorRegistry? Registry { get; set; }
    public Func<OperatorDescriptor, string>? DisplayNameSelector { get; set; }
    public Func<PortDefinition, string>? PortNameSelector { get; set; }
    public Func<WorkflowConnection, string>? ConnectionNameSelector { get; set; }
    public WorkflowNode? SelectedNode { get; private set; }
    public WorkflowConnection? SelectedConnection { get; private set; }
    public event EventHandler<WorkflowNode?>? SelectionChanged;
    public event EventHandler<WorkflowCanvasChangingEventArgs>? WorkflowChanging;
    public event EventHandler<WorkflowCanvasChangedEventArgs>? WorkflowChanged;

    public void ApplyTheme(UiThemeKind theme)
    {
        if (theme == UiThemeKind.Light)
        {
            _canvasBrush = Solid("#FAFAFA"); _nodeBrush = Solid("#FFFFFF"); _selectedNodeBrush = Solid("#EAF4FF");
            _nodeBorderBrush = Solid("#B8B8B8"); _selectedBorderBrush = Solid("#0067B8"); _headerBrush = Solid("#E8E8EA");
            _selectedHeaderBrush = Solid("#CCE8FF"); _nodeTextBrush = Solid("#333333"); _titleTextBrush = Solid("#1F1F1F");
            _portOutlineBrush = Solid("#555555"); _minorGridBrush = Solid("#EEEEEE"); _majorGridBrush = Solid("#D8D8D8"); _shadowBrush = Solid("#26000000");
        }
        else if (theme == UiThemeKind.Gradient)
        {
            _canvasBrush = Gradient("#171927", "#202A42"); _nodeBrush = Solid("#252B3D"); _selectedNodeBrush = Solid("#303852");
            _nodeBorderBrush = Solid("#4A5573"); _selectedBorderBrush = Solid("#817AFF"); _headerBrush = Solid("#333B52");
            _selectedHeaderBrush = Solid("#5147A8"); _nodeTextBrush = Solid("#E2E5F2"); _titleTextBrush = Solid("#F1F3FF");
            _portOutlineBrush = Solid("#D8DCF0"); _minorGridBrush = Solid("#252B3D"); _majorGridBrush = Solid("#35405D"); _shadowBrush = Solid("#80000000");
        }
        else
        {
            _canvasBrush = Solid("#1E1E1E"); _nodeBrush = Solid("#252526"); _selectedNodeBrush = Solid("#2A2D35");
            _nodeBorderBrush = Solid("#454545"); _selectedBorderBrush = Solid("#007ACC"); _headerBrush = Solid("#333337");
            _selectedHeaderBrush = Solid("#094771"); _nodeTextBrush = Solid("#CCCCCC"); _titleTextBrush = Brushes.White;
            _portOutlineBrush = Solid("#D4D4D4"); _minorGridBrush = Solid("#252526"); _majorGridBrush = Solid("#303033"); _shadowBrush = Solid("#70000000");
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(_canvasBrush, Bounds);
        if (Workflow is null || Registry is null) return;
        DrawGrid(context);
        _bounds.Clear();
        using var transform = context.PushTransform(Matrix.CreateTranslation(_panOffset.X, _panOffset.Y));
        foreach (var edge in Workflow.Connections)
        {
            if (!TryGetConnectionPoints(edge, out var from, out var to)) continue;
            var selected = Equals(edge, SelectedConnection);
            var hovered = Equals(edge, _hoveredConnection);
            DrawConnection(context, from, to, selected ? SelectionBrush : hovered ? ConnectionHoverBrush : ConnectionBrush, selected ? 3.5 : hovered ? 2.8 : 1.8);
        }
        if (_dragConnection is { } pending)
            DrawConnection(context, OutputPoint(pending.Node, pending.Index), _connectionPointer, SelectionBrush, 2.5);
        foreach (var node in Workflow.Nodes) DrawNode(context, node);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Workflow is null || Registry is null) return;
        var screenPoint = e.GetPosition(this);
        var point = ToCanvas(screenPoint);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            var edge = FindConnection(point, 20);
            if (edge is not null)
            {
                WorkflowChanging?.Invoke(this, new("删除连接", true));
                Workflow.Connections.Remove(edge);
                if (Equals(SelectedConnection, edge)) SelectedConnection = null;
                WorkflowChanged?.Invoke(this, new(true));
                InvalidateVisual();
            }
            return;
        }

        var outputHit = HitOutput(point);
        if (outputHit is not null)
        {
            Select(outputHit.Value.Node);
            _dragConnection = outputHit;
            _connectionPointer = point;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        var connectionHit = FindConnection(point, 18);
        if (connectionHit is not null)
        {
            SelectConnection(connectionHit);
            return;
        }

        foreach (var node in Workflow.Nodes.AsEnumerable().Reverse())
        {
            if (!_bounds.TryGetValue(node.Id, out var rect) || !rect.Contains(point)) continue;
            Select(node);
            _dragged = node;
            _dragUndoRecorded = false;
            _dragOffset = point - new Point(node.X, node.Y);
            e.Pointer.Capture(this);
            return;
        }
        Select(null);
        _panning = true;
        _panStart = screenPoint;
        _panOrigin = _panOffset;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screenPoint = e.GetPosition(this);
        var point = ToCanvas(screenPoint);
        if (_panning)
        {
            _panOffset = _panOrigin + (screenPoint - _panStart);
            InvalidateVisual();
            return;
        }
        if (_dragConnection is not null)
        {
            _connectionPointer = point;
            _hoveredInput = HitInput(point);
            InvalidateVisual();
            return;
        }
        if (_dragged is null)
        {
            var hovered = FindConnection(point, 18);
            if (!Equals(hovered, _hoveredConnection)) { _hoveredConnection = hovered; InvalidateVisual(); }
            return;
        }
        if (!_dragUndoRecorded)
        {
            WorkflowChanging?.Invoke(this, new("移动算子", false));
            _dragUndoRecorded = true;
        }
        var position = point - _dragOffset;
        _dragged.X = Math.Max(0, position.X);
        _dragged.Y = Math.Max(0, position.Y);
        WorkflowChanged?.Invoke(this, new(false));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragConnection is { } source)
        {
            var input = _hoveredInput ?? HitInput(ToCanvas(e.GetPosition(this)));
            if (input is { } target) CompleteConnection(source, target);
            _dragConnection = null;
            _hoveredInput = null;
            InvalidateVisual();
        }
        _dragged = null;
        _dragUndoRecorded = false;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Delete || Workflow is null) return;
        if (SelectedConnection is not null)
        {
            WorkflowChanging?.Invoke(this, new("删除连接", true));
            Workflow.Connections.Remove(SelectedConnection);
            SelectedConnection = null;
            WorkflowChanged?.Invoke(this, new(true));
            InvalidateVisual();
            return;
        }
        if (SelectedNode is not null)
        {
            WorkflowChanging?.Invoke(this, new("删除算子", true));
            var id = SelectedNode.Id;
            Workflow.Nodes.Remove(SelectedNode);
            Workflow.Connections.RemoveAll(c => c.FromNode == id || c.ToNode == id);
            Select(null);
            WorkflowChanged?.Invoke(this, new(true));
            InvalidateVisual();
        }
    }

    public void Refresh() => InvalidateVisual();

    public bool SelectNodeById(string? nodeId)
    {
        var node = string.IsNullOrWhiteSpace(nodeId) ? null : Workflow?.Nodes.SingleOrDefault(item => item.Id == nodeId);
        if (nodeId is not null && node is null) return false;
        Select(node);
        return true;
    }
    internal WorkflowConnection? HitTestConnection(double x, double y, double maximumDistance = 18) => FindConnection(new Point(x, y), maximumDistance);
    internal Vector PanOffset => _panOffset;
    internal void PanBy(double x, double y) { _panOffset += new Vector(x, y); InvalidateVisual(); }

    public Point FindAvailableNodePosition()
    {
        if (Workflow is null) return new Point(60, 60);
        var columns = Math.Max(1, (int)Math.Floor(Math.Max(240, Bounds.Width - 80) / 240));
        var origin = new Point(Math.Max(24, -_panOffset.X + 48), Math.Max(24, -_panOffset.Y + 48));
        for (var index = 0; index < 2000; index++)
        {
            var candidate = new Rect(origin.X + index % columns * 240, origin.Y + index / columns * 200, NodeWidth, 170);
            if (Workflow.Nodes.All(node => !NodeRect(node).Inflate(16).Intersects(candidate))) return candidate.Position;
        }
        return origin;
    }

    public bool AutoLayout()
    {
        if (Workflow is null || Workflow.Nodes.Count == 0) return false;
        WorkflowChanging?.Invoke(this, new("自动排版", false));
        var levels = Workflow.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
        var indegree = Workflow.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
        var outgoing = Workflow.Nodes.ToDictionary(node => node.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in Workflow.Connections.Where(edge => indegree.ContainsKey(edge.FromNode) && indegree.ContainsKey(edge.ToNode)))
        {
            indegree[edge.ToNode]++;
            outgoing[edge.FromNode].Add(edge.ToNode);
        }
        var queue = new Queue<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        while (queue.TryDequeue(out var id))
            foreach (var next in outgoing[id])
            {
                levels[next] = Math.Max(levels[next], levels[id] + 1);
                if (--indegree[next] == 0) queue.Enqueue(next);
            }
        var origin = new Point(Math.Max(32, -_panOffset.X + 56), Math.Max(32, -_panOffset.Y + 56));
        foreach (var group in Workflow.Nodes.GroupBy(node => levels[node.Id]).OrderBy(group => group.Key))
        {
            var row = 0;
            foreach (var node in group.OrderBy(node => node.Id, StringComparer.Ordinal))
            {
                node.X = Snap(origin.X + group.Key * 280);
                node.Y = Snap(origin.Y + row * 210);
                row++;
            }
        }
        WorkflowChanged?.Invoke(this, new(false));
        InvalidateVisual();
        return true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_dragConnection is null && _hoveredConnection is not null) { _hoveredConnection = null; InvalidateVisual(); }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GraphCanvasAutomationPeer(this);

    private void DrawNode(DrawingContext context, WorkflowNode node)
    {
        if (Registry is null || !Registry.TryGetDescriptor(node.Type, out var descriptor)) return;
        var rows = Math.Max(descriptor.Inputs.Count, descriptor.Outputs.Count);
        var height = HeaderHeight + 18 + Math.Max(1, rows) * PortSpacing;
        var rect = new Rect(node.X, node.Y, NodeWidth, height);
        _bounds[node.Id] = rect;
        context.DrawRectangle(_shadowBrush, null, rect.Translate(new Vector(2, 3)), 5, 5);
        context.DrawRectangle(SelectedNode == node ? _selectedNodeBrush : _nodeBrush, new Pen(SelectedNode == node ? _selectedBorderBrush : _nodeBorderBrush, SelectedNode == node ? 2 : 1), rect, 5, 5);
        context.DrawRectangle(SelectedNode == node ? _selectedHeaderBrush : _headerBrush, null, new Rect(rect.X, rect.Y, rect.Width, HeaderHeight), 5, 5);
        context.DrawRectangle(CategoryBrush(descriptor.Category), null, new Rect(rect.X, rect.Y + 4, 3, HeaderHeight - 8));
        DrawText(context, OperatorName(descriptor), new Point(rect.X + 12, rect.Y + 10), 13, _titleTextBrush);
        for (var i = 0; i < descriptor.Inputs.Count; i++)
        {
            var port = descriptor.Inputs[i]; var p = InputPoint(node, i);
            var active = _hoveredInput?.Node == node && _hoveredInput?.Index == i;
            context.DrawEllipse(active ? SelectionBrush : InputPortBrush, new Pen(_portOutlineBrush, active ? 2 : 1), p, active ? 8 : 6, active ? 8 : 6);
            DrawText(context, PortName(port), p + new Vector(11, -7), 11, _nodeTextBrush);
        }
        for (var i = 0; i < descriptor.Outputs.Count; i++)
        {
            var port = descriptor.Outputs[i]; var p = OutputPoint(node, i);
            var active = _dragConnection?.Node == node && _dragConnection?.Index == i;
            context.DrawEllipse(active ? SelectionBrush : OutputPortBrush, new Pen(_portOutlineBrush, 1), p, 6, 6);
            var text = PortName(port);
            DrawText(context, text, p + new Vector(-12 - text.Length * 11, -7), 11, _nodeTextBrush);
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        var minor = new Pen(_minorGridBrush, 1);
        var major = new Pen(_majorGridBrush, 1);
        var startX = PositiveModulo(_panOffset.X, GridSize);
        var startY = PositiveModulo(_panOffset.Y, GridSize);
        for (var x = startX; x < Bounds.Width; x += GridSize)
        {
            var worldIndex = (int)Math.Round((x - _panOffset.X) / GridSize);
            context.DrawLine(worldIndex % 5 == 0 ? major : minor, new Point(x, 0), new Point(x, Bounds.Height));
        }
        for (var y = startY; y < Bounds.Height; y += GridSize)
        {
            var worldIndex = (int)Math.Round((y - _panOffset.Y) / GridSize);
            context.DrawLine(worldIndex % 5 == 0 ? major : minor, new Point(0, y), new Point(Bounds.Width, y));
        }
    }

    private (WorkflowNode Node, PortDefinition Port, int Index)? HitOutput(Point point)
    {
        if (Workflow is null || Registry is null) return null;
        foreach (var node in Workflow.Nodes.AsEnumerable().Reverse())
        {
            if (!Registry.TryGetDescriptor(node.Type, out var descriptor)) continue;
            for (var i = 0; i < descriptor.Outputs.Count; i++)
                if (Distance(point, OutputPoint(node, i)) <= PortHitRadius) return (node, descriptor.Outputs[i], i);
        }
        return null;
    }

    private (WorkflowNode Node, PortDefinition Port, int Index)? HitInput(Point point)
    {
        if (Workflow is null || Registry is null) return null;
        foreach (var node in Workflow.Nodes.AsEnumerable().Reverse())
        {
            if (!Registry.TryGetDescriptor(node.Type, out var descriptor)) continue;
            for (var i = 0; i < descriptor.Inputs.Count; i++)
            {
                var port = descriptor.Inputs[i];
                if (_dragConnection is { } source && (source.Node.Id == node.Id || port.Type != NodeValueType.Any && source.Port.Type != NodeValueType.Any && port.Type != source.Port.Type)) continue;
                if (Distance(point, InputPoint(node, i)) <= PortHitRadius + 4) return (node, port, i);
            }
        }
        return null;
    }

    private void CompleteConnection((WorkflowNode Node, PortDefinition Port, int Index) source, (WorkflowNode Node, PortDefinition Port, int Index) target)
    {
        if (Workflow is null || source.Node.Id == target.Node.Id) return;
        if (target.Port.Type != NodeValueType.Any && source.Port.Type != NodeValueType.Any && target.Port.Type != source.Port.Type) return;
        WorkflowChanging?.Invoke(this, new("创建连接", true));
        Workflow.Connections.RemoveAll(c => c.ToNode == target.Node.Id && c.ToPort == target.Port.Name);
        Workflow.Connections.Add(new(source.Node.Id, source.Port.Name, target.Node.Id, target.Port.Name));
        WorkflowChanged?.Invoke(this, new(true));
    }

    private bool TryGetConnectionPoints(WorkflowConnection edge, out Point fromPoint, out Point toPoint)
    {
        fromPoint = default; toPoint = default;
        if (Workflow is null || Registry is null) return false;
        var from = Workflow.Nodes.FirstOrDefault(n => n.Id == edge.FromNode); var to = Workflow.Nodes.FirstOrDefault(n => n.Id == edge.ToNode);
        if (from is null || to is null || !Registry.TryGetDescriptor(from.Type, out var fd) || !Registry.TryGetDescriptor(to.Type, out var td)) return false;
        var oi = fd.Outputs.ToList().FindIndex(p => p.Name == edge.FromPort); var ii = td.Inputs.ToList().FindIndex(p => p.Name == edge.ToPort);
        if (oi < 0 || ii < 0) return false;
        fromPoint = OutputPoint(from, oi); toPoint = InputPoint(to, ii); return true;
    }

    private WorkflowConnection? FindConnection(Point point, double maximumDistance)
    {
        if (Workflow is null) return null;
        return Workflow.Connections
            .Select(edge => (Edge: edge, Distance: DistanceToConnection(point, edge)))
            .Where(item => item.Distance <= maximumDistance)
            .OrderBy(item => item.Distance)
            .Select(item => item.Edge)
            .FirstOrDefault();
    }

    private double DistanceToConnection(Point point, WorkflowConnection edge)
    {
        if (!TryGetConnectionPoints(edge, out var a, out var b)) return double.MaxValue;
        var midX = b.X > a.X + 80 ? (a.X + b.X) / 2 : a.X + 60;
        return Math.Min(DistanceToSegment(point, a, new(midX, a.Y)),
            Math.Min(DistanceToSegment(point, new(midX, a.Y), new(midX, b.Y)), DistanceToSegment(point, new(midX, b.Y), b)));
    }

    private static void DrawConnection(DrawingContext context, Point a, Point b, IBrush brush, double width)
    {
        var midX = b.X > a.X + 80 ? (a.X + b.X) / 2 : a.X + 60;
        var verticalDirection = Math.Sign(b.Y - a.Y);
        var finalDirection = Math.Sign(b.X - midX);
        var radius = Math.Min(12, Math.Abs(b.Y - a.Y) / 2);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(a, false);
            if (radius < 1)
            {
                path.LineTo(new(midX, a.Y));
                path.LineTo(b);
            }
            else
            {
                path.LineTo(new(midX - 12, a.Y));
                path.QuadraticBezierTo(new(midX, a.Y), new(midX, a.Y + verticalDirection * radius));
                path.LineTo(new(midX, b.Y - verticalDirection * radius));
                path.QuadraticBezierTo(new(midX, b.Y), new(midX + finalDirection * 12, b.Y));
                path.LineTo(b);
            }
        }
        context.DrawGeometry(null, new Pen(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }

    private void Select(WorkflowNode? node) { SelectedConnection = null; SelectedNode = node; SelectionChanged?.Invoke(this, node); InvalidateVisual(); }
    private void SelectConnection(WorkflowConnection connection) { SelectedNode = null; SelectedConnection = connection; SelectionChanged?.Invoke(this, null); InvalidateVisual(); }
    private static Point InputPoint(WorkflowNode node, int index) => new(node.X, node.Y + HeaderHeight + 20 + index * PortSpacing);
    private static Point OutputPoint(WorkflowNode node, int index) => new(node.X + NodeWidth, node.Y + HeaderHeight + 20 + index * PortSpacing);
    private Point ToCanvas(Point point) => point - _panOffset;
    private Rect NodeRect(WorkflowNode node)
    {
        var rows = Registry is not null && Registry.TryGetDescriptor(node.Type, out var descriptor) ? Math.Max(descriptor.Inputs.Count, descriptor.Outputs.Count) : 1;
        return new Rect(node.X, node.Y, NodeWidth, HeaderHeight + 18 + Math.Max(1, rows) * PortSpacing);
    }
    private static double PositiveModulo(double value, double modulus) => (value % modulus + modulus) % modulus;
    private static double Snap(double value) => Math.Round(value / GridSize) * GridSize;
    private string OperatorName(OperatorDescriptor descriptor) => DisplayNameSelector?.Invoke(descriptor) ?? descriptor.DisplayName;
    private string PortName(PortDefinition port) => PortNameSelector?.Invoke(port) ?? port.DisplayName ?? port.Name;
    private static IBrush CategoryBrush(string category)
    {
        var color = category switch
        {
            "输入" => "#4EC9B0", "图像" or "基础处理" => "#569CD6", "滤波" or "增强" => "#C586C0",
            "阈值" or "二值处理" or "分割" => "#DCDCAA", "形态学" or "边缘" => "#CE9178",
            "轮廓" or "Blob" or "测量" => "#B5CEA8", "逻辑" or "集合" => "#9CDCFE", "AI" => "#C586C0",
            _ => "#808080"
        };
        return new SolidColorBrush(Color.Parse(color));
    }
    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a; var ap = p - a; var lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
        var t = lengthSquared == 0 ? 0 : Math.Clamp((ap.X * ab.X + ap.Y * ab.Y) / lengthSquared, 0, 1);
        return Distance(p, a + ab * t);
    }
    private static void DrawText(DrawingContext context, string text, Point origin, double size, IBrush brush) =>
        context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush), origin);

    private static IBrush Solid(string color) => new SolidColorBrush(Color.Parse(color));
    private static IBrush Gradient(string start, string end) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops { new(Color.Parse(start), 0), new(Color.Parse(end), 1) }
    };

    private sealed class GraphCanvasAutomationPeer(GraphCanvas owner) : ControlAutomationPeer(owner)
    {
        protected override IReadOnlyList<AutomationPeer> GetChildrenCore()
        {
            if (owner.Workflow is null) return [];
            var result = owner.Workflow.Nodes.Select(node => (AutomationPeer)new GraphNodeAutomationPeer(owner, node)).ToList();
            result.AddRange(owner.Workflow.Connections.Select((connection, index) => (AutomationPeer)new GraphConnectionAutomationPeer(owner, connection, index)));
            return result;
        }
    }

    private sealed class GraphNodeAutomationPeer(GraphCanvas owner, WorkflowNode node) : AutomationPeer, IInvokeProvider
    {
        private AutomationPeer? _parent;

        protected override void BringIntoViewCore() { }
        protected override string GetAcceleratorKeyCore() => "";
        protected override string GetAccessKeyCore() => "";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetAutomationIdCore() => "node-" + node.Id;
        protected override Rect GetBoundingRectangleCore()
        {
            var screen = owner.PointToScreen(new Point(node.X + owner._panOffset.X, node.Y + owner._panOffset.Y));
            var descriptor = owner.Registry is not null && owner.Registry.TryGetDescriptor(node.Type, out var value) ? value : null;
            var rows = descriptor is null ? 1 : Math.Max(descriptor.Inputs.Count, descriptor.Outputs.Count);
            return new Rect(screen.X, screen.Y, NodeWidth, HeaderHeight + 18 + Math.Max(1, rows) * PortSpacing);
        }
        protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];
        protected override string GetClassNameCore() => "VisionGraphNode";
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override string GetNameCore() => owner.Registry is not null && owner.Registry.TryGetDescriptor(node.Type, out var descriptor)
            ? owner.OperatorName(descriptor)
            : node.Type;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        protected override bool IsEnabledCore() => true;
        protected override bool IsKeyboardFocusableCore() => true;
        protected override bool HasKeyboardFocusCore() => owner.SelectedNode == node && owner.IsFocused;
        protected override AutomationPeer? GetParentCore() => _parent;
        protected override void SetFocusCore() { owner.Focus(); owner.Select(node); }
        protected override bool ShowContextMenuCore() => false;
        protected override bool TrySetParent(AutomationPeer? parent) { _parent = parent; return true; }
        public void Invoke() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { owner.Focus(); owner.Select(node); });
    }

    private sealed class GraphConnectionAutomationPeer(GraphCanvas owner, WorkflowConnection connection, int index) : AutomationPeer, IInvokeProvider
    {
        private AutomationPeer? _parent;
        protected override void BringIntoViewCore() { }
        protected override string GetAcceleratorKeyCore() => "";
        protected override string GetAccessKeyCore() => "";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetAutomationIdCore() => $"connection-{index}-{connection.FromNode}-{connection.ToNode}";
        protected override Rect GetBoundingRectangleCore()
        {
            if (!owner.TryGetConnectionPoints(connection, out var from, out var to)) return default;
            var topLeft = owner.PointToScreen(new Point(Math.Min(from.X, to.X) + owner._panOffset.X, Math.Min(from.Y, to.Y) + owner._panOffset.Y));
            return new Rect(topLeft.X, topLeft.Y, Math.Max(1, Math.Abs(to.X - from.X)), Math.Max(18, Math.Abs(to.Y - from.Y)));
        }
        protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];
        protected override string GetClassNameCore() => "VisionGraphConnection";
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override string GetNameCore() => owner.ConnectionNameSelector?.Invoke(connection) ?? $"{connection.FromPort} → {connection.ToPort}";
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        protected override bool IsEnabledCore() => true;
        protected override bool IsKeyboardFocusableCore() => true;
        protected override bool HasKeyboardFocusCore() => Equals(owner.SelectedConnection, connection) && owner.IsFocused;
        protected override AutomationPeer? GetParentCore() => _parent;
        protected override void SetFocusCore() { owner.Focus(); owner.SelectConnection(connection); }
        protected override bool ShowContextMenuCore() => false;
        protected override bool TrySetParent(AutomationPeer? parent) { _parent = parent; return true; }
        public void Invoke() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { owner.Focus(); owner.SelectConnection(connection); });
    }
}
