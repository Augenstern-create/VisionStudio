using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AddNodeEdit), "addNode")]
[JsonDerivedType(typeof(RemoveNodeEdit), "removeNode")]
[JsonDerivedType(typeof(SetParameterEdit), "setParameter")]
[JsonDerivedType(typeof(RenameNodeEdit), "renameNode")]
[JsonDerivedType(typeof(MoveNodeEdit), "moveNode")]
[JsonDerivedType(typeof(ConnectEdit), "connect")]
[JsonDerivedType(typeof(DisconnectEdit), "disconnect")]
public abstract record WorkflowEdit;

public sealed record AddNodeEdit(
    string NodeId,
    string OperatorType,
    double? X = null,
    double? Y = null,
    string? Name = null,
    IReadOnlyDictionary<string, JsonElement>? Parameters = null) : WorkflowEdit;

public sealed record RemoveNodeEdit(string NodeId) : WorkflowEdit;
public sealed record SetParameterEdit(string NodeId, string Parameter, JsonElement Value) : WorkflowEdit;
public sealed record RenameNodeEdit(string NodeId, string? Name) : WorkflowEdit;
public sealed record MoveNodeEdit(string NodeId, double X, double Y) : WorkflowEdit;
public sealed record ConnectEdit(string FromNode, string FromPort, string ToNode, string ToPort) : WorkflowEdit;
public sealed record DisconnectEdit(string FromNode, string FromPort, string ToNode, string ToPort) : WorkflowEdit;

public sealed record WorkflowMutationRequest(
    string RequestId,
    long ExpectedRevision,
    IReadOnlyList<WorkflowEdit> Edits,
    string? Summary = null,
    string? SelectNodeId = null,
    string? ExecuteNodeId = null);

public sealed record WorkflowSnapshot(
    long Revision,
    Guid ProjectId,
    string ProjectName,
    WorkflowDocument Workflow);

public sealed record WorkflowMutationResult(
    string RequestId,
    long Revision,
    bool Applied,
    WorkflowDocument Workflow,
    string? SelectedNodeId,
    WorkflowAutomationRunResult? Execution = null);

public sealed record WorkflowAutomationOutput(
    string Port,
    NodeValueType Type,
    string? Summary = null,
    string? ArtifactPath = null);

public sealed record WorkflowAutomationRunResult(
    string RequestId,
    string TargetNodeId,
    bool Success,
    IReadOnlyList<WorkflowAutomationOutput> Outputs,
    IReadOnlyList<NodeTiming> Timings,
    string? Error = null);

public sealed record WorkflowChangedEventArgs(
    string RequestId,
    long Revision,
    WorkflowDocument PreviousWorkflow,
    WorkflowDocument Workflow,
    string Summary,
    string? SelectedNodeId);

public sealed record WorkflowExecutionCompletedEventArgs(WorkflowAutomationRunResult Result);

public interface IWorkflowAutomationRunner {
  Task<WorkflowAutomationRunResult> RunAsync(
      string requestId,
      ProjectHandle project,
      WorkflowDocument workflow,
      string targetNodeId,
      CancellationToken cancellationToken = default);
}

/// <summary>
/// Stable, UI-independent entry point for assistants and other automation clients.
/// Adapters such as an in-process assistant, HTTP endpoint, or MCP server should
/// expose this contract instead of editing controls or workflow files.
/// </summary>
public interface IWorkflowAutomationApi {
  event EventHandler<WorkflowChangedEventArgs>? WorkflowChanged;
  event EventHandler<WorkflowExecutionCompletedEventArgs>? ExecutionCompleted;

  Task<IReadOnlyList<OperatorDescriptor>> GetOperatorCatalogAsync(CancellationToken cancellationToken = default);
  Task<WorkflowSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
  Task<WorkflowMutationResult> ApplyAsync(WorkflowMutationRequest request, CancellationToken cancellationToken = default);
  Task<WorkflowAutomationRunResult> ExecuteAsync(string requestId, string targetNodeId, CancellationToken cancellationToken = default);
}

public sealed class WorkflowAutomationApiHost {
  public IWorkflowAutomationApi? Current { get; private set; }
  public event EventHandler<IWorkflowAutomationApi?>? CurrentChanged;

  public void Attach(IWorkflowAutomationApi? api) {
    if (ReferenceEquals(Current, api)) {
      return;
    }

    Current = api;
    CurrentChanged?.Invoke(this, api);
  }
}

public sealed class WorkflowAutomationSession(
    ProjectHandle project,
    WorkflowDocument workflow,
    ProjectService projects,
    OperatorRegistry registry,
    IWorkflowAutomationRunner runner) : IWorkflowAutomationApi {
  private readonly ProjectHandle _project = project;
  private readonly ProjectService _projects = projects;
  private readonly OperatorRegistry _registry = registry;
  private readonly IWorkflowAutomationRunner _runner = runner;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private readonly Dictionary<string, WorkflowMutationResult> _completedRequests = new(StringComparer.Ordinal);
  private WorkflowDocument _workflow = workflow;
  private long _revision;

  public event EventHandler<WorkflowChangedEventArgs>? WorkflowChanged;
  public event EventHandler<WorkflowExecutionCompletedEventArgs>? ExecutionCompleted;

  public Task<IReadOnlyList<OperatorDescriptor>> GetOperatorCatalogAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult<IReadOnlyList<OperatorDescriptor>>([.. _registry.Descriptors]);
  }

  public async Task<WorkflowSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) {
    await _gate.WaitAsync(cancellationToken);
    try { return new(_revision, _project.Project.Id, _project.Project.Name, Clone(_workflow)); } finally { _gate.Release(); }
  }

  public async Task<WorkflowMutationResult> ApplyAsync(WorkflowMutationRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
    if (request.Edits is not { Count: > 0 }) {
      throw new ArgumentException("At least one workflow edit is required.", nameof(request));
    }

    WorkflowChangedEventArgs changed;
    WorkflowMutationResult result;
    await _gate.WaitAsync(cancellationToken);
    try {
      if (_completedRequests.TryGetValue(request.RequestId, out var completed)) {
        return completed;
      }

      if (request.ExpectedRevision != _revision) {
        throw new WorkflowRevisionConflictException(request.ExpectedRevision, _revision);
      }

      var previous = Clone(_workflow);
      var candidate = Clone(_workflow);
      foreach (var edit in request.Edits) {
        ApplyEdit(candidate, edit);
      }

      var validation = WorkflowValidator.Validate(candidate, _registry, requireConnectedInputs: false);
      if (!validation.IsValid) {
        throw new WorkflowValidationException(validation.Errors);
      }

      EnsureRequestedNodeExists(candidate, request.SelectNodeId, nameof(request.SelectNodeId));
      EnsureRequestedNodeExists(candidate, request.ExecuteNodeId, nameof(request.ExecuteNodeId));

      await _projects.SaveWorkflowAsync(_project, candidate, createSnapshot: true, cancellationToken);
      _workflow = candidate;
      _revision++;
      string summary = string.IsNullOrWhiteSpace(request.Summary) ? "AI workflow update" : request.Summary.Trim();
      await _projects.AppendOperationAsync(_project, new ProjectOperation(DateTimeOffset.Now, "AI 工作流修改", summary), cancellationToken);
      result = new(request.RequestId, _revision, true, Clone(candidate), request.SelectNodeId);
      Remember(result);
      changed = new(request.RequestId, _revision, previous, Clone(candidate), summary, request.SelectNodeId);
    } finally { _gate.Release(); }

    WorkflowChanged?.Invoke(this, changed);
    if (!string.IsNullOrWhiteSpace(request.ExecuteNodeId)) {
      var execution = await ExecuteAsync(request.RequestId, request.ExecuteNodeId, cancellationToken);
      result = result with { Execution = execution };
      await _gate.WaitAsync(cancellationToken);
      try { _completedRequests[request.RequestId] = result; } finally { _gate.Release(); }
    }
    return result;
  }

  public async Task<WorkflowAutomationRunResult> ExecuteAsync(string requestId, string targetNodeId, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
    ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
    WorkflowDocument snapshot;
    await _gate.WaitAsync(cancellationToken);
    try {
      EnsureRequestedNodeExists(_workflow, targetNodeId, nameof(targetNodeId));
      snapshot = Clone(_workflow);
    } finally { _gate.Release(); }

    WorkflowAutomationRunResult result;
    try { result = await _runner.RunAsync(requestId, _project, snapshot, targetNodeId, cancellationToken); } catch (OperationCanceledException) { throw; } catch (Exception exception) {
      result = new(requestId, targetNodeId, false, [], [], exception.Message);
    }
    ExecutionCompleted?.Invoke(this, new(result));
    return result;
  }

  // UI-originated edits currently mutate the live document through GraphCanvas.
  // These two host hooks keep revision tracking coherent without exposing UI types
  // in the automation contract. New external integrations must use ApplyAsync.
  public void NotifyHostChanged() {
    Interlocked.Increment(ref _revision);
  }

  public void SynchronizeFromHost(WorkflowDocument workflow, bool incrementRevision = true) {
    _workflow = workflow;
    if (incrementRevision) {
      Interlocked.Increment(ref _revision);
    }
  }

  private void ApplyEdit(WorkflowDocument workflow, WorkflowEdit edit) {
    switch (edit) {
      case AddNodeEdit add:
        AddNode(workflow, add);
        break;
      case RemoveNodeEdit remove:
        var removed = FindNode(workflow, remove.NodeId);
        workflow.Nodes.Remove(removed);
        workflow.Connections.RemoveAll(edge => edge.FromNode == remove.NodeId || edge.ToNode == remove.NodeId);
        break;
      case SetParameterEdit parameter:
        SetParameter(FindNode(workflow, parameter.NodeId), parameter.Parameter, parameter.Value);
        break;
      case RenameNodeEdit rename:
        FindNode(workflow, rename.NodeId).Name = string.IsNullOrWhiteSpace(rename.Name) ? null : rename.Name.Trim();
        break;
      case MoveNodeEdit move:
        EnsureFinite(move.X, nameof(move.X)); EnsureFinite(move.Y, nameof(move.Y));
        var moved = FindNode(workflow, move.NodeId); moved.X = move.X; moved.Y = move.Y;
        break;
      case ConnectEdit connect:
        workflow.Connections.Add(new(connect.FromNode, connect.FromPort, connect.ToNode, connect.ToPort));
        break;
      case DisconnectEdit disconnect:
        var edge = new WorkflowConnection(disconnect.FromNode, disconnect.FromPort, disconnect.ToNode, disconnect.ToPort);
        if (!workflow.Connections.Remove(edge)) {
          throw new KeyNotFoundException("The requested connection does not exist.");
        }

        break;
      default:
        throw new NotSupportedException($"Unsupported workflow edit '{edit.GetType().Name}'.");
    }
  }

  private void AddNode(WorkflowDocument workflow, AddNodeEdit edit) {
    ArgumentException.ThrowIfNullOrWhiteSpace(edit.NodeId);
    if (workflow.Nodes.Any(node => node.Id == edit.NodeId)) {
      throw new InvalidOperationException($"Node '{edit.NodeId}' already exists.");
    }

    if (!_registry.TryGetDescriptor(edit.OperatorType, out var descriptor)) {
      throw new KeyNotFoundException($"Unknown operator '{edit.OperatorType}'.");
    }

    var (X, Y) = FindAvailablePosition(workflow);
    double x = edit.X ?? X;
    double y = edit.Y ?? Y;
    EnsureFinite(x, nameof(edit.X)); EnsureFinite(y, nameof(edit.Y));
    var node = new WorkflowNode { Id = edit.NodeId, Type = descriptor.Type, Name = edit.Name ?? descriptor.DisplayName, X = x, Y = y };
    foreach (var parameter in descriptor.Parameters) {
      node.SetParameter(parameter.Name, parameter.DefaultValue);
    }

    if (edit.Parameters is not null) {
      foreach (var parameter in edit.Parameters) {
        SetParameter(node, parameter.Key, parameter.Value);
      }
    }

    workflow.Nodes.Add(node);
  }

  private void SetParameter(WorkflowNode node, string name, JsonElement value) {
    if (!_registry.TryGetDescriptor(node.Type, out var descriptor)) {
      throw new KeyNotFoundException($"Unknown operator '{node.Type}'.");
    }

    var definition = descriptor.Parameters.SingleOrDefault(item => item.Name == name)
        ?? throw new KeyNotFoundException($"Operator '{node.Type}' has no parameter '{name}'.");
    ValidateParameter(definition, value);
    node.Parameters[name] = value.Clone();
  }

  private static void ValidateParameter(ParameterDefinition definition, JsonElement value) {
    bool kindMatches = definition.Kind switch {
      JsonValueKind.True or JsonValueKind.False => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
      _ => value.ValueKind == definition.Kind
    };
    if (!kindMatches) {
      throw new ArgumentException($"Parameter '{definition.Name}' requires JSON kind {definition.Kind}.");
    }

    if (definition.Kind == JsonValueKind.Number) {
      double number = value.GetDouble();
      if (definition.Minimum is { } minimum && number < minimum || definition.Maximum is { } maximum && number > maximum) {
        throw new ArgumentOutOfRangeException(definition.Name, number, $"Parameter must be between {definition.Minimum} and {definition.Maximum}.");
      }
    }
  }

  private static WorkflowNode FindNode(WorkflowDocument workflow, string nodeId) {
    return workflow.Nodes.SingleOrDefault(node => node.Id == nodeId) ?? throw new KeyNotFoundException($"Node '{nodeId}' does not exist.");
  }

  private static void EnsureRequestedNodeExists(WorkflowDocument workflow, string? nodeId, string argument) {
    if (!string.IsNullOrWhiteSpace(nodeId) && workflow.Nodes.All(node => node.Id != nodeId)) {
      throw new ArgumentException($"Node '{nodeId}' does not exist.", argument);
    }
  }

  private static (double X, double Y) FindAvailablePosition(WorkflowDocument workflow) {
    for (int index = 0; ; index++) {
      double x = 60d + (index % 5) * 220d;
      double y = 60d + (index / 5) * 130d;
      if (workflow.Nodes.All(node => Math.Abs(node.X - x) > 170 || Math.Abs(node.Y - y) > 90)) {
        return (x, y);
      }
    }
  }

  private static void EnsureFinite(double value, string name) {
    if (!double.IsFinite(value)) {
      throw new ArgumentOutOfRangeException(name, "Coordinates must be finite.");
    }
  }

  private void Remember(WorkflowMutationResult result) {
    _completedRequests[result.RequestId] = result;
    while (_completedRequests.Count > 100) {
      _completedRequests.Remove(_completedRequests.Keys.First());
    }
  }

  private static WorkflowDocument Clone(WorkflowDocument workflow) {
    return WorkflowJson.Deserialize(WorkflowJson.Serialize(workflow));
  }
}

public sealed class WorkflowRevisionConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException($"Workflow revision conflict: expected {expectedRevision}, actual {actualRevision}.") {
  public long ExpectedRevision { get; } = expectedRevision;
  public long ActualRevision { get; } = actualRevision;
}
