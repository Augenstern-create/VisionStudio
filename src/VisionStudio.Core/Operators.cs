using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisionStudio.Core;

public interface IVisionOperator {
  ValueTask ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken);
}

public sealed class OperatorRegistry {
  private readonly Dictionary<string, (OperatorDescriptor Descriptor, Func<IVisionOperator> Factory)> _items = new(StringComparer.Ordinal);

  public IReadOnlyCollection<OperatorDescriptor> Descriptors =>
      new ReadOnlyCollection<OperatorDescriptor>([.. _items.Values.Select(x => x.Descriptor).OrderBy(x => x.Category).ThenBy(x => x.DisplayName)]);

  public void Register(OperatorDescriptor descriptor, Func<IVisionOperator> factory) {
    ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Type);
    if (!_items.TryAdd(descriptor.Type, (descriptor, factory))) {
      throw new InvalidOperationException($"Operator '{descriptor.Type}' is already registered.");
    }
  }

  public bool TryGetDescriptor(string type, out OperatorDescriptor descriptor) {
    if (_items.TryGetValue(type, out var item)) {
      descriptor = item.Descriptor;
      return true;
    }
    descriptor = null!;
    return false;
  }

  public IVisionOperator Create(string type) {
    return _items.TryGetValue(type, out var item)
          ? item.Factory()
          : throw new KeyNotFoundException($"Unknown operator '{type}'.");
  }
}

public sealed class NodeExecutionContext {
  private readonly IReadOnlyDictionary<string, object> _inputs;
  private readonly Dictionary<string, object> _outputs = new(StringComparer.Ordinal);

  internal NodeExecutionContext(WorkflowNode node, IReadOnlyDictionary<string, object> inputs, string workingDirectory) {
    Node = node;
    _inputs = inputs;
    WorkingDirectory = workingDirectory;
  }

  public WorkflowNode Node { get; }
  public string WorkingDirectory { get; }
  public IReadOnlyDictionary<string, object> Outputs => _outputs;

  public T GetInput<T>(string port) {
    return _inputs.TryGetValue(port, out object? value) && value is T typed
      ? typed
      : throw new InvalidOperationException($"Input '{Node.Id}.{port}' is missing or is not {typeof(T).Name}.");
  }

  public bool TryGetInput<T>(string port, out T? value) {
    if (_inputs.TryGetValue(port, out object? raw) && raw is T typed) {
      value = typed;
      return true;
    }
    value = default;
    return false;
  }

  public T GetParameter<T>(string name, T fallback) {
    return Node.GetParameter(name, fallback);
  }

  public string ResolveProjectPath(string path) {
    return ProjectPaths.ResolveInside(WorkingDirectory, path);
  }

  public void SetOutput(string port, object value) {
    ArgumentNullException.ThrowIfNull(value);
    if (!_outputs.TryAdd(port, value)) {
      throw new InvalidOperationException($"Output '{Node.Id}.{port}' was assigned more than once.");
    }
  }
}

public sealed record NodeTiming(
    string NodeId,
    TimeSpan Duration,
    bool FromCache = false,
    bool FromExternalInput = false);

public sealed record WorkflowExternalOutput(
    string NodeId,
    string Port,
    object Value);

/// <summary>Per-node progress emitted while a workflow slice executes, in topological order.</summary>
public sealed record WorkflowProgress(int Index, int Total, string NodeId, string NodeType, TimeSpan Duration, bool FromCache);

public interface INodeOutputCache {
  string GetExternalFingerprint(WorkflowNode node, string workingDirectory);
  bool TryRestore(WorkflowNode node, OperatorDescriptor descriptor, string cacheKey, out IReadOnlyDictionary<string, object> outputs);
  ValueTask StoreAsync(WorkflowNode node, OperatorDescriptor descriptor, string cacheKey, IReadOnlyDictionary<string, object> outputs, CancellationToken cancellationToken);
}

public sealed class WorkflowExecutionResult : IDisposable {
  private readonly Dictionary<(string Node, string Port), object> _values;
  private bool _disposed;

  internal WorkflowExecutionResult(Dictionary<(string, string), object> values, IReadOnlyList<NodeTiming> timings) {
    _values = values;
    Timings = timings;
  }

  public IReadOnlyList<NodeTiming> Timings { get; }
  public T GetOutput<T>(string nodeId, string port) {
    return _values.TryGetValue((nodeId, port), out object? value) && value is T typed
          ? typed
          : throw new KeyNotFoundException($"Output '{nodeId}.{port}' was not produced.");
  }

  public bool TryGetOutput<T>(string nodeId, string port, out T? value) {
    if (_values.TryGetValue((nodeId, port), out object? raw) && raw is T typed) {
      value = typed;
      return true;
    }
    value = default;
    return false;
  }

  public IReadOnlyDictionary<string, object> GetNodeOutputs(string nodeId) {
    return _values
      .Where(pair => pair.Key.Node == nodeId)
      .ToDictionary(pair => pair.Key.Port, pair => pair.Value, StringComparer.Ordinal);
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;
    DisposeDistinct(_values.Values);
    _values.Clear();
  }

  internal static void DisposeDistinct(IEnumerable<object> values) {
    var seen = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
    foreach (var disposable in values.OfType<IDisposable>()) {
      if (seen.Add(disposable)) {
        disposable.Dispose();
      }
    }
  }
}

public sealed class WorkflowExecutor(OperatorRegistry registry, string? workingDirectory = null, INodeOutputCache? cache = null) {
  private readonly string _workingDirectory = Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory());

  /// <summary>Optional per-node progress sink. Callbacks come from the executing thread.</summary>
  public IProgress<WorkflowProgress>? Progress { get; set; }
  public Task<WorkflowExecutionResult> ExecuteAsync(WorkflowDocument workflow, CancellationToken cancellationToken = default) {
    return ExecuteAsync(workflow, targetNodeId: null, [], cancellationToken);
  }

  public async Task<WorkflowExecutionResult> ExecuteAsync(WorkflowDocument workflow, string? targetNodeId, CancellationToken cancellationToken = default) {
    return await ExecuteAsync(workflow, targetNodeId, [], cancellationToken);
  }

  public async Task<WorkflowExecutionResult> ExecuteAsync(
      WorkflowDocument workflow,
      string? targetNodeId,
      IReadOnlyList<WorkflowExternalOutput> externalOutputs,
      CancellationToken cancellationToken = default) {
    var executionWorkflow = CreateExecutionSlice(workflow, targetNodeId);
    var validation = WorkflowValidator.Validate(executionWorkflow, registry);
    if (!validation.IsValid) {
      throw new WorkflowValidationException(validation.Errors);
    }

    var values = new Dictionary<(string Node, string Port), object>();
    var cacheKeys = new Dictionary<string, string>(StringComparer.Ordinal);
    var timings = new List<NodeTiming>();
    var supplied = externalOutputs
        .GroupBy(output => output.NodeId, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => group.ToDictionary(output => output.Port, output => output.Value),
            StringComparer.Ordinal);
    try {
      int completed = 0;
      int total = validation.TopologicalOrder.Count;
      foreach (var node in validation.TopologicalOrder) {
        cancellationToken.ThrowIfCancellationRequested();
        registry.TryGetDescriptor(node.Type, out var descriptor);
        if (supplied.TryGetValue(node.Id, out var external)) {
          AddExternalOutputs(node, descriptor, external, values);
          cacheKeys[node.Id] = $"external:{Guid.NewGuid():N}";
          timings.Add(new NodeTiming(
              node.Id,
              TimeSpan.Zero,
              FromExternalInput: true));
          continue;
        }
        var inbound = executionWorkflow.Connections.Where(c => c.ToNode == node.Id).ToList();
        string cacheKey = ComputeCacheKey(node, inbound, cacheKeys, cache?.GetExternalFingerprint(node, _workingDirectory));
        cacheKeys[node.Id] = cacheKey;
        if (cache is not null && cache.TryRestore(node, descriptor, cacheKey, out var restored)) {
          foreach (var output in restored) {
            values.Add((node.Id, output.Key), output.Value);
          }

          timings.Add(new NodeTiming(node.Id, TimeSpan.Zero, FromCache: true));
          Progress?.Report(new WorkflowProgress(++completed, total, node.Id, node.Type, TimeSpan.Zero, FromCache: true));
          continue;
        }
        var inputs = inbound
            .Where(c => c.ToNode == node.Id)
            .ToDictionary(c => c.ToPort, c => values[(c.FromNode, c.FromPort)], StringComparer.Ordinal);
        var context = new NodeExecutionContext(node, inputs, _workingDirectory);
        var stopwatch = Stopwatch.StartNew();
        try {
          await registry.Create(node.Type).ExecuteAsync(context, cancellationToken);
          foreach (var output in descriptor.Outputs) {
            if (!context.Outputs.ContainsKey(output.Name)) {
              throw new InvalidOperationException($"Operator '{node.Type}' did not produce declared output '{output.Name}'.");
            }
          }
        } catch {
          WorkflowExecutionResult.DisposeDistinct(context.Outputs.Values);
          throw;
        }
        stopwatch.Stop();
        timings.Add(new NodeTiming(node.Id, stopwatch.Elapsed));
        Progress?.Report(new WorkflowProgress(++completed, total, node.Id, node.Type, stopwatch.Elapsed, FromCache: false));
        foreach (var output in context.Outputs) {
          values.Add((node.Id, output.Key), output.Value);
        }

        if (cache is not null) {
          await cache.StoreAsync(node, descriptor, cacheKey, context.Outputs, cancellationToken);
        }
      }
      return new WorkflowExecutionResult(values, timings);
    } catch {
      WorkflowExecutionResult.DisposeDistinct(values.Values);
      throw;
    }
  }

  private static void AddExternalOutputs(
      WorkflowNode node,
      OperatorDescriptor descriptor,
      IReadOnlyDictionary<string, object> external,
      IDictionary<(string Node, string Port), object> values) {
    string[] missing = [.. descriptor.Outputs
        .Where(output => !external.ContainsKey(output.Name))
        .Select(output => output.Name)];
    if (missing.Length > 0) {
      throw new InvalidOperationException(
          $"External input for '{node.Id}' is missing output(s): {string.Join(", ", missing)}.");
    }
    string[] unknown = [.. external.Keys
        .Where(port => descriptor.Outputs.All(output => output.Name != port))];
    if (unknown.Length > 0) {
      string ports = string.Join(", ", unknown);
      throw new InvalidOperationException(
          $"External input for '{node.Id}' contains unknown output(s): {ports}.");
    }
    foreach (var output in external) {
      values.Add((node.Id, output.Key), output.Value);
    }
  }

  private static WorkflowDocument CreateExecutionSlice(WorkflowDocument workflow, string? targetNodeId) {
    if (targetNodeId is null) {
      return workflow;
    }

    if (workflow.Nodes.All(n => n.Id != targetNodeId)) {
      throw new KeyNotFoundException($"Node '{targetNodeId}' does not exist.");
    }

    var included = new HashSet<string>(StringComparer.Ordinal) { targetNodeId };
    var pending = new Stack<string>();
    pending.Push(targetNodeId);
    while (pending.TryPop(out string? current)) {
      foreach (var edge in workflow.Connections.Where(c => c.ToNode == current)) {
        if (included.Add(edge.FromNode)) {
          pending.Push(edge.FromNode);
        }
      }
    }
    var slice = new WorkflowDocument { Name = workflow.Name };
    slice.Nodes.AddRange(workflow.Nodes.Where(n => included.Contains(n.Id)));
    slice.Connections.AddRange(workflow.Connections.Where(c => included.Contains(c.FromNode) && included.Contains(c.ToNode)));
    return slice;
  }

  private static string ComputeCacheKey(WorkflowNode node, IReadOnlyList<WorkflowConnection> inbound, IReadOnlyDictionary<string, string> upstreamKeys, string? externalFingerprint) {
    var builder = new StringBuilder(node.Type).Append('|');
    foreach (var parameter in node.Parameters.OrderBy(x => x.Key, StringComparer.Ordinal)) {
      builder.Append(parameter.Key).Append('=').Append(parameter.Value.GetRawText()).Append('|');
    }

    foreach (var edge in inbound.OrderBy(x => x.ToPort, StringComparer.Ordinal)) {
      builder.Append(edge.ToPort).Append("<-").Append(edge.FromPort).Append(':').Append(upstreamKeys.GetValueOrDefault(edge.FromNode)).Append('|');
    }

    builder.Append(externalFingerprint);
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
  }
}
