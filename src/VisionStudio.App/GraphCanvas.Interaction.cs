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
  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    base.OnPointerPressed(e);
    Focus();
    if (Workflow is null || Registry is null) {
      return;
    }

    var screenPoint = e.GetPosition(this);
    var point = ToCanvas(screenPoint);
    var properties = e.GetCurrentPoint(this).Properties;
    if (properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed) {
      var edge = FindConnection(point, 20);
      if (edge is not null) {
        ShowConnectionContextMenu(edge);
      }

      return;
    }

    var referenceNode = HitNodeReference(point);
    if (referenceNode is not null) {
      Select(referenceNode);
      BeginNodeReferenceDrag(e, referenceNode);
      return;
    }

    var outputHit = HitOutput(point);
    if (outputHit is not null) {
      Select(outputHit.Value.Node);
      _dragConnection = outputHit;
      _connectionPointer = point;
      e.Pointer.Capture(this);
      InvalidateVisual();
      return;
    }

    var connectionHit = FindConnection(point, 18);
    if (connectionHit is not null) {
      SelectConnection(connectionHit);
      return;
    }

    foreach (var node in Workflow.Nodes.AsEnumerable().Reverse()) {
      if (!_bounds.TryGetValue(node.Id, out var rect) || !rect.Contains(point)) {
        continue;
      }

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
    _panOrigin = PanOffset;
    e.Pointer.Capture(this);
  }

  protected override void OnPointerMoved(PointerEventArgs e) {
    base.OnPointerMoved(e);
    var screenPoint = e.GetPosition(this);
    var point = ToCanvas(screenPoint);
    if (_panning) {
      PanOffset = _panOrigin + (screenPoint - _panStart);
      InvalidateVisual();
      return;
    }
    if (_dragConnection is not null) {
      _connectionPointer = point;
      _hoveredInput = HitInput(point);
      InvalidateVisual();
      return;
    }
    if (_dragged is null) {
      var hovered = FindConnection(point, 18);
      if (!Equals(hovered, _hoveredConnection)) { _hoveredConnection = hovered; InvalidateVisual(); }
      return;
    }
    if (!_dragUndoRecorded) {
      WorkflowChanging?.Invoke(this, new("移动算子", false));
      _dragUndoRecorded = true;
    }
    var position = point - _dragOffset;
    _dragged.X = Math.Max(0, position.X);
    _dragged.Y = Math.Max(0, position.Y);
    WorkflowChanged?.Invoke(this, new(false));
    InvalidateVisual();
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e) {
    base.OnPointerReleased(e);
    if (_dragConnection is { } source) {
      var input = _hoveredInput ?? HitInput(ToCanvas(e.GetPosition(this)));
      if (input is { } target) {
        CompleteConnection(source, target);
      }

      _dragConnection = null;
      _hoveredInput = null;
      InvalidateVisual();
    }
    _dragged = null;
    _dragUndoRecorded = false;
    _panning = false;
    e.Pointer.Capture(null);
  }

  protected override void OnKeyDown(KeyEventArgs e) {
    base.OnKeyDown(e);
    if (e.Key != Key.Delete || Workflow is null) {
      return;
    }

    if (SelectedConnection is not null) {
      WorkflowChanging?.Invoke(this, new("删除连接", true));
      Workflow.Connections.Remove(SelectedConnection);
      SelectedConnection = null;
      WorkflowChanged?.Invoke(this, new(true));
      InvalidateVisual();
      return;
    }
    if (SelectedNode is not null) {
      WorkflowChanging?.Invoke(this, new("删除算子", true));
      var ids = _selectedNodeIds.Count > 0 ? _selectedNodeIds : [SelectedNode.Id];
      Workflow.Nodes.RemoveAll(node => ids.Contains(node.Id));
      Workflow.Connections.RemoveAll(c => ids.Contains(c.FromNode) || ids.Contains(c.ToNode));
      Select(null);
      WorkflowChanged?.Invoke(this, new(true));
      InvalidateVisual();
    }
  }

  protected override void OnPointerExited(PointerEventArgs e) {
    base.OnPointerExited(e);
    if (_dragConnection is null && _hoveredConnection is not null) { _hoveredConnection = null; InvalidateVisual(); }
  }

  private WorkflowNode? HitNodeReference(Point point) {
    if (Workflow is null || NodeReferenceSelector is null) {
      return null;
    }

    return Workflow.Nodes.AsEnumerable().Reverse().FirstOrDefault(node =>
        !string.IsNullOrWhiteSpace(NodeReferenceSelector(node)) && ReferenceBadgeRect(node).Contains(point));
  }

  private async void BeginNodeReferenceDrag(PointerPressedEventArgs trigger, WorkflowNode node) {
    string? reference = NodeReferenceSelector?.Invoke(node);
    if (string.IsNullOrWhiteSpace(reference)) {
      return;
    }

    var data = new DataTransfer();
    data.Add(DataTransferItem.CreateText($"[{reference}] {node.Name ?? node.Type}"));
    await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy);
  }

  private (WorkflowNode Node, PortDefinition Port, int Index)? HitOutput(Point point) {
    if (Workflow is null || Registry is null) {
      return null;
    }

    foreach (var node in Workflow.Nodes.AsEnumerable().Reverse()) {
      if (!Registry.TryGetDescriptor(node.Type, out var descriptor)) {
        continue;
      }

      for (int i = 0; i < descriptor.Outputs.Count; i++) {
        if (Distance(point, OutputPoint(node, i)) <= PortHitRadius) {
          return (node, descriptor.Outputs[i], i);
        }
      }
    }
    return null;
  }

  private (WorkflowNode Node, PortDefinition Port, int Index)? HitInput(Point point) {
    if (Workflow is null || Registry is null) {
      return null;
    }

    foreach (var node in Workflow.Nodes.AsEnumerable().Reverse()) {
      if (!Registry.TryGetDescriptor(node.Type, out var descriptor)) {
        continue;
      }

      for (int i = 0; i < descriptor.Inputs.Count; i++) {
        var port = descriptor.Inputs[i];
        if (_dragConnection is { } source && (source.Node.Id == node.Id || port.Type != NodeValueType.Any && source.Port.Type != NodeValueType.Any && port.Type != source.Port.Type)) {
          continue;
        }

        if (Distance(point, InputPoint(node, i)) <= PortHitRadius + 4) {
          return (node, port, i);
        }
      }
    }
    return null;
  }

  private void CompleteConnection((WorkflowNode Node, PortDefinition Port, int Index) source, (WorkflowNode Node, PortDefinition Port, int Index) target) {
    if (Workflow is null || source.Node.Id == target.Node.Id) {
      return;
    }

    if (target.Port.Type != NodeValueType.Any && source.Port.Type != NodeValueType.Any && target.Port.Type != source.Port.Type) {
      return;
    }

    WorkflowChanging?.Invoke(this, new("创建连接", true));
    Workflow.Connections.RemoveAll(c => c.ToNode == target.Node.Id && c.ToPort == target.Port.Name);
    Workflow.Connections.Add(new(source.Node.Id, source.Port.Name, target.Node.Id, target.Port.Name));
    WorkflowChanged?.Invoke(this, new(true));
  }

  private bool TryGetConnectionPoints(WorkflowConnection edge, out Point fromPoint, out Point toPoint) {
    fromPoint = default; toPoint = default;
    if (Workflow is null || Registry is null) {
      return false;
    }

    var from = Workflow.Nodes.FirstOrDefault(n => n.Id == edge.FromNode); var to = Workflow.Nodes.FirstOrDefault(n => n.Id == edge.ToNode);
    if (from is null || to is null || !Registry.TryGetDescriptor(from.Type, out var fd) || !Registry.TryGetDescriptor(to.Type, out var td)) {
      return false;
    }

    int oi = fd.Outputs.ToList().FindIndex(p => p.Name == edge.FromPort); int ii = td.Inputs.ToList().FindIndex(p => p.Name == edge.ToPort);
    if (oi < 0 || ii < 0) {
      return false;
    }

    fromPoint = OutputPoint(from, oi); toPoint = InputPoint(to, ii); return true;
  }

  private WorkflowConnection? FindConnection(Point point, double maximumDistance) {
    if (Workflow is null) {
      return null;
    }

    return Workflow.Connections
        .Select(edge => (Edge: edge, Distance: DistanceToConnection(point, edge)))
        .Where(item => item.Distance <= maximumDistance)
        .OrderBy(item => item.Distance)
        .Select(item => item.Edge)
        .FirstOrDefault();
  }

  private double DistanceToConnection(Point point, WorkflowConnection edge) {
    if (!TryGetConnectionPoints(edge, out var a, out var b)) {
      return double.MaxValue;
    }

    double midX = b.X > a.X + 80 ? (a.X + b.X) / 2 : a.X + 60;
    return Math.Min(DistanceToSegment(point, a, new(midX, a.Y)),
        Math.Min(DistanceToSegment(point, new(midX, a.Y), new(midX, b.Y)), DistanceToSegment(point, new(midX, b.Y), b)));
  }

  private void ShowConnectionContextMenu(WorkflowConnection edge) {
    SelectConnection(edge);
    var select = new MenuItem { Header = SelectDownstreamText };
    select.Click += (_, _) => SelectDownstream(edge);
    var layout = new MenuItem { Header = LayoutDownstreamText };
    layout.Click += (_, _) => AutoLayoutDownstream(edge);
    var deleteBranch = new MenuItem { Header = DeleteDownstreamText };
    deleteBranch.Click += (_, _) => DeleteDownstream(edge);
    var deleteConnection = new MenuItem { Header = DeleteConnectionText };
    deleteConnection.Click += (_, _) => {
      if (Workflow is null) {
        return;
      }

      WorkflowChanging?.Invoke(this, new("删除连接", true));
      Workflow.Connections.Remove(edge);
      Select(null);
      WorkflowChanged?.Invoke(this, new(true));
    };
    var menu = new ContextMenu { ItemsSource = new object[] { select, layout, deleteBranch, new Separator(), deleteConnection } };
    menu.Open(this);
  }

  private void Select(WorkflowNode? node) { _selectedNodeIds.Clear(); if (node is not null) { _selectedNodeIds.Add(node.Id); } SelectedConnection = null; SelectedNode = node; SelectionChanged?.Invoke(this, node); InvalidateVisual(); }

  private void SelectConnection(WorkflowConnection connection) { _selectedNodeIds.Clear(); SelectedNode = null; SelectedConnection = connection; SelectionChanged?.Invoke(this, null); InvalidateVisual(); }

  private Point ToCanvas(Point point) {
    return point - PanOffset;
  }

  private Rect NodeRect(WorkflowNode node) {
    int rows = Registry is not null && Registry.TryGetDescriptor(node.Type, out var descriptor) ? Math.Max(descriptor.Inputs.Count, descriptor.Outputs.Count) : 1;
    return new Rect(node.X, node.Y, NodeWidth, HeaderHeight + 18 + Math.Max(1, rows) * PortSpacing);
  }

  private static double Distance(Point a, Point b) {
    return Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
  }

  private static double DistanceToSegment(Point p, Point a, Point b) {
    var ab = b - a; var ap = p - a; double lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
    double t = lengthSquared == 0 ? 0 : Math.Clamp((ap.X * ab.X + ap.Y * ab.Y) / lengthSquared, 0, 1);
    return Distance(p, a + ab * t);
  }

}
