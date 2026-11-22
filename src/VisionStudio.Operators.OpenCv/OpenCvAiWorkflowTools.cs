using System.Globalization;
using System.Text;
using System.Text.Json;
using Emgu.CV;
using VisionStudio.Ai.Abstractions;
using VisionStudio.Core;

namespace VisionStudio.Operators.OpenCv;

/// <summary>
/// Read-only probes the assistant can call from the chat panel. Runs on a cloned workflow so the
/// user's canvas, undo stack and project file are never touched, and every call happens on a
/// thread-pool thread because operator execution is CPU-bound.
/// </summary>
public sealed class OpenCvAiWorkflowTools(
    OperatorRegistry registry,
    string projectRoot,
    Func<WorkflowDocument> workflowProvider,
    Action<AiSweepSeries>? onSweep = null) : IAiWorkflowTools {
  private const int MaximumValues = 12;

  public Task<string> ExecuteNodeAsync(string nodeId, CancellationToken cancellationToken = default) {
    var workflow = Snapshot();
    return Task.Run(async () => (await RunOnceAsync(workflow, nodeId, cancellationToken)).Text, cancellationToken);
  }

  public Task<string> SweepParameterAsync(AiParameterSweep sweep, CancellationToken cancellationToken = default) {
    var workflow = Snapshot();
    return Task.Run(() => RunSweepAsync(workflow, sweep, cancellationToken), cancellationToken);
  }

  private WorkflowDocument Snapshot() {
    // Called on the UI thread before the background run starts, so the snapshot cannot race edits.
    return WorkflowJson.Deserialize(WorkflowJson.Serialize(workflowProvider()));
  }

  private async Task<string> RunSweepAsync(WorkflowDocument workflow, AiParameterSweep sweep, CancellationToken cancellationToken) {
    var node = workflow.Nodes.FirstOrDefault(item => item.Id == sweep.NodeId);
    if (node is null) {
      return DescribeMissingNode(workflow, sweep.NodeId);
    }

    if (!registry.TryGetDescriptor(node.Type, out var descriptor)) {
      return $"error: unknown operator '{node.Type}' on node '{sweep.NodeId}'";
    }

    if (descriptor.Parameters.All(parameter => parameter.Name != sweep.Parameter)) {
      string available = string.Join(", ", descriptor.Parameters.Select(parameter => parameter.Name));
      return $"error: '{node.Type}' has no parameter '{sweep.Parameter}'. Available: {(available.Length == 0 ? "(none)" : available)}";
    }

    if (sweep.Values.Count == 0 || sweep.Values.Count > MaximumValues) {
      return $"error: a sweep needs 1..{MaximumValues} values";
    }

    string target = string.IsNullOrWhiteSpace(sweep.TargetNodeId) ? sweep.NodeId : sweep.TargetNodeId;
    if (workflow.Nodes.All(item => item.Id != target)) {
      return DescribeMissingNode(workflow, target);
    }

    var lines = new List<string> {
      $"swept {sweep.NodeId}.{sweep.Parameter} over {sweep.Values.Count} values; measured node '{target}'"
    };
    var points = new List<AiSweepPoint>();
    foreach (string value in sweep.Values) {
      cancellationToken.ThrowIfCancellationRequested();
      node.Parameters[sweep.Parameter] = ToParameterElement(value);
      var (outcome, metrics, milliseconds) = await RunOnceAsync(workflow, target, cancellationToken);
      lines.Add($"{sweep.Parameter}={value} -> {outcome}");
      points.Add(new AiSweepPoint(value, metrics, milliseconds));
    }

    string metric = points.SelectMany(point => point.Metrics.Keys).FirstOrDefault() ?? "";
    if (metric.Length > 0) {
      onSweep?.Invoke(new AiSweepSeries(sweep.NodeId, sweep.Parameter, target, points, metric));
    }

    return string.Join("\n", lines);
  }

  private async Task<(string Text, IReadOnlyDictionary<string, double> Metrics, double Milliseconds)> RunOnceAsync(
      WorkflowDocument workflow, string nodeId, CancellationToken cancellationToken) {
    if (workflow.Nodes.All(node => node.Id != nodeId)) {
      return (DescribeMissingNode(workflow, nodeId), new Dictionary<string, double>(), 0);
    }

    try {
      using var result = await new WorkflowExecutor(registry, projectRoot).ExecuteAsync(workflow, nodeId, cancellationToken);
      double milliseconds = result.Timings.Sum(timing => timing.Duration.TotalMilliseconds);
      var values = result.GetNodeOutputs(nodeId);
      // Image sizes repeat identically for every swept value; keep them only when nothing else exists.
      var interesting = values.Where(pair => pair.Value is not Mat).ToList();
      var selected = interesting.Count > 0 ? interesting : values.ToList();
      var parts = selected.Select(pair => $"{pair.Key}={Summarize(pair.Value)}").ToList();
      string body = parts.Count == 0 ? "no outputs" : string.Join(", ", parts);
      return ($"{milliseconds:F0} ms, {body}", NumericMetrics(interesting), milliseconds);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception exception) {
      return ("error: " + exception.Message.ReplaceLineEndings(" "), new Dictionary<string, double>(), 0);
    }
  }

  private static IReadOnlyDictionary<string, double> NumericMetrics(IEnumerable<KeyValuePair<string, object>> values) {
    var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
    foreach (var (port, value) in values) {
      switch (value) {
        case System.Collections.ICollection collection:
          metrics[port] = collection.Count;
          break;
        case IConvertible convertible when value is not string:
          try {
            metrics[port] = convertible.ToDouble(CultureInfo.InvariantCulture);
          } catch (Exception) {
            // Non-numeric payloads are not chartable.
          }

          break;
      }
    }

    return metrics;
  }

  private static string DescribeMissingNode(WorkflowDocument workflow, string nodeId) {
    string known = string.Join(", ", workflow.Nodes.Take(30).Select(node => node.Id));
    return $"error: node '{nodeId}' does not exist. Known ids: {known}";
  }

  private static string Summarize(object value) {
    return value switch {
      Mat image => $"{image.Width}x{image.Height}x{image.NumberOfChannels}",
      string text => text.Length <= 60 ? text : text[..60] + "…",
      System.Collections.ICollection collection => $"Count({collection.Count})",
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      _ => value.ToString() ?? value.GetType().Name
    };
  }

  private static JsonElement ToParameterElement(string value) {
    try {
      return JsonSerializer.Deserialize<JsonElement>(value);
    } catch (JsonException) {
      return JsonSerializer.SerializeToElement(value);
    }
  }
}
