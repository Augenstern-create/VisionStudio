using System.Globalization;
using System.Text.Json;
using VisionStudio.Core;

namespace VisionStudio.App;

/// <summary>One recorded node execution, kept for later comparison.</summary>
public sealed record ExecutionRecord(
    string Id,
    DateTime At,
    string NodeId,
    string NodeName,
    string NodeType,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyDictionary<string, string> Outputs,
    double Milliseconds) {
  public string Caption => $"{At:HH:mm:ss} · {NodeName} · {Milliseconds:F0} ms";
}

/// <summary>
/// Per-project execution history stored in <c>.project/executions.json</c>. Kept small on purpose:
/// the newest <see cref="Capacity"/> runs are enough to answer "what changed since last time".
/// </summary>
public sealed class ExecutionHistoryStore {
  public const int Capacity = 100;
  private readonly string _path;

  public ExecutionHistoryStore(string projectRoot) {
    _path = ProjectPaths.ResolveInside(projectRoot, ".project/executions.json");
  }

  public List<ExecutionRecord> Records { get; } = [];

  public static string Summarize(object? value) {
    return value switch {
      Emgu.CV.Mat image => $"{image.Width}x{image.Height}x{image.NumberOfChannels}",
      string text => text.Length <= 80 ? text : text[..80] + "…",
      System.Collections.ICollection collection => $"Count({collection.Count})",
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      null => "",
      _ => value.ToString() ?? value.GetType().Name
    };
  }

  public async Task LoadAsync(CancellationToken cancellationToken = default) {
    Records.Clear();
    if (!File.Exists(_path)) {
      return;
    }

    try {
      var stored = JsonSerializer.Deserialize<List<ExecutionRecord>>(
          await File.ReadAllTextAsync(_path, cancellationToken), WorkflowJson.Options) ?? [];
      Records.AddRange(stored.TakeLast(Capacity));
    } catch (Exception) {
      // A corrupt history must never block opening a project.
      Records.Clear();
    }
  }

  public async Task SaveAsync(CancellationToken cancellationToken = default) {
    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    string temporary = _path + ".tmp";
    await File.WriteAllTextAsync(temporary,
        JsonSerializer.Serialize(Records.TakeLast(Capacity).ToList(), WorkflowJson.Options), cancellationToken);
    File.Move(temporary, _path, overwrite: true);
  }

  public async Task AddAsync(ExecutionRecord record, CancellationToken cancellationToken = default) {
    Records.Add(record);
    while (Records.Count > Capacity) {
      Records.RemoveAt(0);
    }

    try {
      await SaveAsync(cancellationToken);
    } catch (IOException) {
      // Recording history is best-effort; failing to persist must not fail the execution.
    }
  }

  public static ExecutionRecord Create(WorkflowNode node, IReadOnlyDictionary<string, string> outputs, double milliseconds) {
    return new ExecutionRecord(
        Guid.NewGuid().ToString("N"),
        DateTime.Now,
        node.Id,
        node.Name ?? node.Type,
        node.Type,
        node.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal),
        outputs,
        milliseconds);
  }
}
