using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VisionStudio.Core;

namespace VisionStudio.App;

public sealed record WorkflowCanvasChangedEventArgs(bool AffectsExecution);
public sealed record WorkflowCanvasChangingEventArgs(string Description, bool AffectsExecution);

public sealed partial class GraphCanvas : Control {
  private const double NodeWidth = 184;
  private const double HeaderHeight = 36;
  private const double PortSpacing = 28;
  private const double PortHitRadius = 19;
  private const double GridSize = 32;
  public OperatorRegistry? Registry { get; set; }
  public Func<OperatorDescriptor, string>? DisplayNameSelector { get; set; }
  public Func<PortDefinition, string>? PortNameSelector { get; set; }
  public Func<WorkflowConnection, string>? ConnectionNameSelector { get; set; }
  public Func<WorkflowNode, string?>? NodeReferenceSelector { get; set; }
  public string SelectDownstreamText { get; set; } = "选择后续节点";
  public string LayoutDownstreamText { get; set; } = "整理后续节点";
  public string DeleteDownstreamText { get; set; } = "删除后续节点";
  public string DeleteConnectionText { get; set; } = "删除连接";
  public WorkflowNode? SelectedNode { get; private set; }
  public WorkflowConnection? SelectedConnection { get; private set; }
  public IReadOnlyCollection<string> SelectedNodeIds => _selectedNodeIds;
  public event EventHandler<WorkflowNode?>? SelectionChanged;
  public event EventHandler<WorkflowCanvasChangingEventArgs>? WorkflowChanging;
  public event EventHandler<WorkflowCanvasChangedEventArgs>? WorkflowChanged;









  internal Vector PanOffset { get; private set; }








  protected override AutomationPeer OnCreateAutomationPeer() {
    return new GraphCanvasAutomationPeer(this);
  }

























  private sealed class GraphCanvasAutomationPeer(GraphCanvas owner) : ControlAutomationPeer(owner) {
    protected override IReadOnlyList<AutomationPeer> GetChildrenCore() {
      if (owner.Workflow is null) {
        return [];
      }

      var result = owner.Workflow.Nodes.Select(node => (AutomationPeer)new GraphNodeAutomationPeer(owner, node)).ToList();
      result.AddRange(owner.Workflow.Connections.Select((connection, index) => (AutomationPeer)new GraphConnectionAutomationPeer(owner, connection, index)));
      return result;
    }
  }

  private sealed class GraphNodeAutomationPeer(GraphCanvas owner, WorkflowNode node) : AutomationPeer, IInvokeProvider {
    private AutomationPeer? _parent;

    protected override void BringIntoViewCore() { }
    protected override string GetAcceleratorKeyCore() {
      return "";
    }

    protected override string GetAccessKeyCore() {
      return "";
    }

    protected override AutomationControlType GetAutomationControlTypeCore() {
      return AutomationControlType.Button;
    }

    protected override string GetAutomationIdCore() {
      return "node-" + node.Id;
    }

    protected override Rect GetBoundingRectangleCore() {
      var screen = owner.PointToScreen(new Point(node.X + owner.PanOffset.X, node.Y + owner.PanOffset.Y));
      var descriptor = owner.Registry is not null && owner.Registry.TryGetDescriptor(node.Type, out var value) ? value : null;
      int rows = descriptor is null ? 1 : Math.Max(descriptor.Inputs.Count, descriptor.Outputs.Count);
      return new Rect(screen.X, screen.Y, NodeWidth, HeaderHeight + 18 + Math.Max(1, rows) * PortSpacing);
    }
    protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() {
      return [];
    }

    protected override string GetClassNameCore() {
      return "VisionGraphNode";
    }

    protected override AutomationPeer? GetLabeledByCore() {
      return null;
    }

    protected override string GetNameCore() {
      return owner.Registry is not null && owner.Registry.TryGetDescriptor(node.Type, out var descriptor)
            ? owner.OperatorName(descriptor)
            : node.Type;
    }

    protected override bool IsContentElementCore() {
      return true;
    }

    protected override bool IsControlElementCore() {
      return true;
    }

    protected override bool IsEnabledCore() {
      return true;
    }

    protected override bool IsKeyboardFocusableCore() {
      return true;
    }

    protected override bool HasKeyboardFocusCore() {
      return owner.SelectedNode == node && owner.IsFocused;
    }

    protected override AutomationPeer? GetParentCore() {
      return _parent;
    }

    protected override void SetFocusCore() { owner.Focus(); owner.Select(node); }
    protected override bool ShowContextMenuCore() {
      return false;
    }

    protected override bool TrySetParent(AutomationPeer? parent) { _parent = parent; return true; }
    public void Invoke() {
      Avalonia.Threading.Dispatcher.UIThread.Post(() => { owner.Focus(); owner.Select(node); });
    }
  }

  private sealed class GraphConnectionAutomationPeer(GraphCanvas owner, WorkflowConnection connection, int index) : AutomationPeer, IInvokeProvider {
    private AutomationPeer? _parent;
    protected override void BringIntoViewCore() { }
    protected override string GetAcceleratorKeyCore() {
      return "";
    }

    protected override string GetAccessKeyCore() {
      return "";
    }

    protected override AutomationControlType GetAutomationControlTypeCore() {
      return AutomationControlType.ListItem;
    }

    protected override string GetAutomationIdCore() {
      return $"connection-{index}-{connection.FromNode}-{connection.ToNode}";
    }

    protected override Rect GetBoundingRectangleCore() {
      if (!owner.TryGetConnectionPoints(connection, out var from, out var to)) {
        return default;
      }

      var topLeft = owner.PointToScreen(new Point(Math.Min(from.X, to.X) + owner.PanOffset.X, Math.Min(from.Y, to.Y) + owner.PanOffset.Y));
      return new Rect(topLeft.X, topLeft.Y, Math.Max(1, Math.Abs(to.X - from.X)), Math.Max(18, Math.Abs(to.Y - from.Y)));
    }
    protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() {
      return [];
    }

    protected override string GetClassNameCore() {
      return "VisionGraphConnection";
    }

    protected override AutomationPeer? GetLabeledByCore() {
      return null;
    }

    protected override string GetNameCore() {
      return owner.ConnectionNameSelector?.Invoke(connection) ?? $"{connection.FromPort} → {connection.ToPort}";
    }

    protected override bool IsContentElementCore() {
      return true;
    }

    protected override bool IsControlElementCore() {
      return true;
    }

    protected override bool IsEnabledCore() {
      return true;
    }

    protected override bool IsKeyboardFocusableCore() {
      return true;
    }

    protected override bool HasKeyboardFocusCore() {
      return Equals(owner.SelectedConnection, connection) && owner.IsFocused;
    }

    protected override AutomationPeer? GetParentCore() {
      return _parent;
    }

    protected override void SetFocusCore() { owner.Focus(); owner.SelectConnection(connection); }
    protected override bool ShowContextMenuCore() {
      return false;
    }

    protected override bool TrySetParent(AutomationPeer? parent) { _parent = parent; return true; }
    public void Invoke() {
      Avalonia.Threading.Dispatcher.UIThread.Post(() => { owner.Focus(); owner.SelectConnection(connection); });
    }
  }
}
