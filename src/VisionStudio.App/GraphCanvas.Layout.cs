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
  public void Refresh() {
    InvalidateVisual();
  }

  public bool SelectNodeById(string? nodeId) {
    var node = string.IsNullOrWhiteSpace(nodeId) ? null : Workflow?.Nodes.SingleOrDefault(item => item.Id == nodeId);
    if (nodeId is not null && node is null) {
      return false;
    }

    Select(node);
    return true;
  }

  internal WorkflowConnection? HitTestConnection(double x, double y, double maximumDistance = 18) {
    return FindConnection(new Point(x, y), maximumDistance);
  }

  internal void PanBy(double x, double y) { PanOffset += new Vector(x, y); InvalidateVisual(); }

  public Point FindAvailableNodePosition() {
    if (Workflow is null) {
      return new Point(60, 60);
    }

    int columns = Math.Max(1, (int)Math.Floor(Math.Max(240, Bounds.Width - 80) / 240));
    var origin = new Point(Math.Max(24, -PanOffset.X + 48), Math.Max(24, -PanOffset.Y + 48));
    for (int index = 0; index < 2000; index++) {
      var candidate = new Rect(origin.X + index % columns * 240, origin.Y + index / columns * 200, NodeWidth, 170);
      if (Workflow.Nodes.All(node => !NodeRect(node).Inflate(16).Intersects(candidate))) {
        return candidate.Position;
      }
    }
    return origin;
  }

  public bool AutoLayout() {
    if (Workflow is null || Workflow.Nodes.Count == 0) {
      return false;
    }

    WorkflowChanging?.Invoke(this, new("自动排版", false));
    var levels = Workflow.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
    var indegree = Workflow.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
    var outgoing = Workflow.Nodes.ToDictionary(node => node.Id, _ => new List<string>(), StringComparer.Ordinal);
    foreach (var edge in Workflow.Connections.Where(edge => indegree.ContainsKey(edge.FromNode) && indegree.ContainsKey(edge.ToNode))) {
      indegree[edge.ToNode]++;
      outgoing[edge.FromNode].Add(edge.ToNode);
    }
    var queue = new Queue<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
    while (queue.TryDequeue(out string? id)) {
      foreach (string? next in outgoing[id]) {
        levels[next] = Math.Max(levels[next], levels[id] + 1);
        if (--indegree[next] == 0) {
          queue.Enqueue(next);
        }
      }
    }

    var origin = new Point(Math.Max(32, -PanOffset.X + 56), Math.Max(32, -PanOffset.Y + 56));
    foreach (var group in Workflow.Nodes.GroupBy(node => levels[node.Id]).OrderBy(group => group.Key)) {
      int row = 0;
      foreach (var node in group.OrderBy(node => node.Id, StringComparer.Ordinal)) {
        node.X = Snap(origin.X + group.Key * 280);
        node.Y = Snap(origin.Y + row * 210);
        row++;
      }
    }
    WorkflowChanged?.Invoke(this, new(false));
    InvalidateVisual();
    return true;
  }

  internal IReadOnlySet<string> GetDownstreamNodeIds(WorkflowConnection edge) {
    if (Workflow is null) {
      return new HashSet<string>(StringComparer.Ordinal);
    }

    var result = new HashSet<string>(StringComparer.Ordinal);
    var queue = new Queue<string>();
    queue.Enqueue(edge.ToNode);
    while (queue.TryDequeue(out string? nodeId)) {
      if (!result.Add(nodeId)) {
        continue;
      }

      foreach (string? next in Workflow.Connections.Where(item => item.FromNode == nodeId).Select(item => item.ToNode)) {
        queue.Enqueue(next);
      }
    }
    return result;
  }

  internal void SelectDownstream(WorkflowConnection edge) {
    if (Workflow is null) {
      return;
    }

    _selectedNodeIds.Clear();
    _selectedNodeIds.UnionWith(GetDownstreamNodeIds(edge));
    SelectedConnection = edge;
    SelectedNode = Workflow.Nodes.FirstOrDefault(node => node.Id == edge.ToNode);
    SelectionChanged?.Invoke(this, SelectedNode);
    InvalidateVisual();
  }

  internal bool AutoLayoutDownstream(WorkflowConnection edge) {
    if (Workflow is null) {
      return false;
    }

    var ids = GetDownstreamNodeIds(edge);
    if (ids.Count == 0) {
      return false;
    }

    WorkflowChanging?.Invoke(this, new("整理后续算子", false));
    var levels = ids.ToDictionary(id => id, _ => int.MaxValue, StringComparer.Ordinal);
    levels[edge.ToNode] = 0;
    var queue = new Queue<string>();
    queue.Enqueue(edge.ToNode);
    while (queue.TryDequeue(out string? current)) {
      foreach (string? next in Workflow.Connections.Where(item => item.FromNode == current && ids.Contains(item.ToNode)).Select(item => item.ToNode)) {
        if (levels[next] > levels[current] + 1) {
          levels[next] = levels[current] + 1;
          queue.Enqueue(next);
        }
      }
    }

    var target = Workflow.Nodes.Single(node => node.Id == edge.ToNode);
    var outside = Workflow.Nodes.Where(node => !ids.Contains(node.Id)).ToList();
    var placed = new List<Rect>();
    foreach (var group in ids.Select(id => Workflow.Nodes.Single(node => node.Id == id))
                 .GroupBy(node => levels[node.Id] == int.MaxValue ? 0 : levels[node.Id]).OrderBy(group => group.Key)) {
      int row = 0;
      foreach (var node in group.OrderBy(node => node.Id, StringComparer.Ordinal)) {
        var candidate = new Rect(Snap(target.X + group.Key * 280), Snap(target.Y + row * 210), NodeWidth, 170);
        while (outside.Any(other => NodeRect(other).Inflate(16).Intersects(candidate)) || placed.Any(rect => rect.Inflate(16).Intersects(candidate))) {
          candidate = candidate.Translate(new Vector(0, 210));
        }

        node.X = candidate.X;
        node.Y = candidate.Y;
        placed.Add(candidate);
        row++;
      }
    }
    SelectDownstream(edge);
    WorkflowChanged?.Invoke(this, new(false));
    return true;
  }

  internal bool DeleteDownstream(WorkflowConnection edge) {
    if (Workflow is null) {
      return false;
    }

    var ids = GetDownstreamNodeIds(edge);
    if (ids.Count == 0) {
      return false;
    }

    WorkflowChanging?.Invoke(this, new("删除后续算子", true));
    Workflow.Nodes.RemoveAll(node => ids.Contains(node.Id));
    Workflow.Connections.RemoveAll(connection => ids.Contains(connection.FromNode) || ids.Contains(connection.ToNode));
    Select(null);
    WorkflowChanged?.Invoke(this, new(true));
    return true;
  }

  private static Point InputPoint(WorkflowNode node, int index) {
    return new(node.X, node.Y + HeaderHeight + 20 + index * PortSpacing);
  }

  private static Point OutputPoint(WorkflowNode node, int index) {
    return new(node.X + NodeWidth, node.Y + HeaderHeight + 20 + index * PortSpacing);
  }

  private static double PositiveModulo(double value, double modulus) {
    return (value % modulus + modulus) % modulus;
  }

  private static double Snap(double value) {
    return Math.Round(value / GridSize) * GridSize;
  }

}
