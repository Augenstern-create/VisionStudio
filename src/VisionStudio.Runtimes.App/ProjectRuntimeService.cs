using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Emgu.CV;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Runtimes.App;

public sealed record RuntimeProjectContext(
    ProjectHandle Project,
    WorkflowDocument Workflow,
    RuntimeProfile Profile,
    OperatorRegistry Registry);

public sealed record RuntimeResultValue(
    string Name,
    string NodeId,
    string Port,
    RuntimeResultRole Role,
    string DisplayValue,
    string? Unit = null,
    string? ImagePath = null) {
  public string NodePort => $"{NodeId}.{Port}";
  public string DisplayText => string.IsNullOrWhiteSpace(Unit)
      ? DisplayValue
      : $"{DisplayValue} {Unit}";
}

public sealed record RuntimeInspectionResult(
    string RunId,
    long Sequence,
    DateTimeOffset CapturedAt,
    DateTimeOffset CompletedAt,
    string SourceName,
    string? PrimaryImagePath,
    IReadOnlyList<RuntimeResultValue> Values,
    IReadOnlyList<NodeTiming> Timings,
    long ElapsedMilliseconds);

public sealed class ProjectRuntimeService(
    ProjectService? projects = null,
    RuntimeProfileStore? profiles = null,
    FrameSourceFactory? sources = null) {
  private readonly ProjectService _projects = projects ?? new ProjectService();
  private readonly RuntimeProfileStore _profiles = profiles ?? new RuntimeProfileStore();
  private readonly FrameSourceFactory _sources = sources ?? new FrameSourceFactory();

  public async Task<RuntimeProjectContext> OpenAsync(
      string projectPath,
      CancellationToken cancellationToken = default) {
    var project = await _projects.OpenAsync(projectPath, cancellationToken);
    var workflow = await _projects.LoadWorkflowAsync(project, cancellationToken);
    var registry = OpenCvOperatorCatalog.CreateRegistry();
    var profile = await _profiles.LoadOrCreateAsync(
        project,
        workflow,
        registry,
        cancellationToken);
    Validate(project, workflow, profile, registry);
    return new RuntimeProjectContext(project, workflow, profile, registry);
  }

  public Task SaveProfileAsync(
      RuntimeProjectContext context,
      CancellationToken cancellationToken = default) {
    Validate(context.Project, context.Workflow, context.Profile, context.Registry);
    return _profiles.SaveAsync(context.Project, context.Profile, cancellationToken);
  }

  public async Task<RuntimeInspectionResult> RunOnceAsync(
      RuntimeProjectContext context,
      CancellationToken cancellationToken = default) {
    Validate(context.Project, context.Workflow, context.Profile, context.Registry);
    await using var source = _sources.Create(
        context.Profile.Source,
        context.Project.RootPath);
    await source.OpenAsync(cancellationToken);
    using var frame = await source.CaptureAsync(cancellationToken);
    return await ExecuteFrameAsync(context, frame, cancellationToken);
  }

  public async Task<RuntimeInspectionResult> ExecuteFrameAsync(
      RuntimeProjectContext context,
      CapturedFrame frame,
      CancellationToken cancellationToken = default) {
    Validate(context.Project, context.Workflow, context.Profile, context.Registry);
    var injected = frame.Image.Clone();
    var stopwatch = Stopwatch.StartNew();
    WorkflowExecutionResult? execution = null;
    try {
      var executor = new WorkflowExecutor(
          context.Registry,
          context.Project.RootPath);
      execution = await executor.ExecuteAsync(
          context.Workflow,
          context.Profile.TargetNodeId,
          [new WorkflowExternalOutput(
              context.Profile.InputNodeId,
              "Image",
              injected)],
          cancellationToken);
      stopwatch.Stop();
      string runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{frame.Sequence:D6}";
      var values = ExtractResults(
          context,
          execution,
          runId,
          cancellationToken);
      string? primary = values.FirstOrDefault(
          value => value.Role == RuntimeResultRole.PrimaryImage)?.ImagePath
          ?? values.FirstOrDefault(value => value.ImagePath is not null)?.ImagePath;
      return new RuntimeInspectionResult(
          runId,
          frame.Sequence,
          frame.CapturedAt,
          DateTimeOffset.UtcNow,
          frame.SourceName,
          primary,
          values,
          execution.Timings,
          stopwatch.ElapsedMilliseconds);
    } finally {
      execution?.Dispose();
    }
  }

  public static void Validate(
      ProjectHandle project,
      WorkflowDocument workflow,
      RuntimeProfile profile,
      OperatorRegistry registry) {
    var validation = WorkflowValidator.Validate(workflow, registry);
    if (!validation.IsValid) {
      throw new WorkflowValidationException(validation.Errors);
    }
    var input = workflow.Nodes.SingleOrDefault(
        node => node.Id == profile.InputNodeId)
        ?? throw new InvalidDataException(
            $"Runtime input node '{profile.InputNodeId}' does not exist.");
    if (input.Type != "Input.RuntimeFrame") {
      throw new InvalidDataException(
          $"Runtime input node '{input.Id}' must use Input.RuntimeFrame.");
    }
    if (workflow.Nodes.All(node => node.Id != profile.TargetNodeId)) {
      throw new InvalidDataException(
          $"Runtime target node '{profile.TargetNodeId}' does not exist.");
    }
    var executionNodes = ExecutionNodes(
        workflow,
        profile.TargetNodeId);
    if (!executionNodes.Contains(profile.InputNodeId)) {
      throw new InvalidDataException(
          $"Runtime target '{profile.TargetNodeId}' does not depend on input "
          + $"'{profile.InputNodeId}'.");
    }
    if (profile.ResultMappings.Count == 0) {
      throw new InvalidDataException("Runtime profile has no result mappings.");
    }
    foreach (var mapping in profile.ResultMappings) {
      var node = workflow.Nodes.SingleOrDefault(item => item.Id == mapping.NodeId)
          ?? throw new InvalidDataException(
              $"Result mapping node '{mapping.NodeId}' does not exist.");
      if (!executionNodes.Contains(mapping.NodeId)) {
        throw new InvalidDataException(
            $"Result mapping node '{mapping.NodeId}' is outside the target execution path.");
      }
      registry.TryGetDescriptor(node.Type, out var descriptor);
      if (descriptor.Outputs.All(output => output.Name != mapping.Port)) {
        throw new InvalidDataException(
            $"Result mapping output '{mapping.NodeId}.{mapping.Port}' does not exist.");
      }
    }
    _ = ProjectPaths.ResolveInside(project.RootPath, "images/output/runtime");
  }

  private static IReadOnlySet<string> ExecutionNodes(
      WorkflowDocument workflow,
      string targetNodeId) {
    var included = new HashSet<string>(StringComparer.Ordinal) { targetNodeId };
    var pending = new Stack<string>();
    pending.Push(targetNodeId);
    while (pending.TryPop(out string? current)) {
      foreach (var edge in workflow.Connections.Where(
          connection => connection.ToNode == current)) {
        if (included.Add(edge.FromNode)) {
          pending.Push(edge.FromNode);
        }
      }
    }
    return included;
  }

  private static IReadOnlyList<RuntimeResultValue> ExtractResults(
      RuntimeProjectContext context,
      WorkflowExecutionResult execution,
      string runId,
      CancellationToken cancellationToken) {
    var values = new List<RuntimeResultValue>();
    foreach (var mapping in context.Profile.ResultMappings) {
      cancellationToken.ThrowIfCancellationRequested();
      var nodeOutputs = execution.GetNodeOutputs(
          mapping.NodeId);
      if (!nodeOutputs.TryGetValue(mapping.Port, out object? value)) {
        throw new InvalidOperationException(
            $"Mapped result '{mapping.NodeId}.{mapping.Port}' was not produced.");
      }
      if (value is Mat image) {
        string relative = $"images/output/runtime/{Safe(runId)}-{Safe(mapping.Name)}.png";
        string path = ProjectPaths.ResolveInside(context.Project.RootPath, relative);
        OpenCvFileCodec.Write(path, image);
        values.Add(new RuntimeResultValue(
            mapping.Name,
            mapping.NodeId,
            mapping.Port,
            mapping.Role,
            $"{image.Width} × {image.Height} · {image.NumberOfChannels} channel(s)",
            mapping.Unit,
            relative));
      } else {
        values.Add(new RuntimeResultValue(
            mapping.Name,
            mapping.NodeId,
            mapping.Port,
            mapping.Role,
            Summarize(value),
            mapping.Unit));
      }
    }
    return values;
  }

  private static string Summarize(object value) {
    return value switch {
      string text => text,
      ICollection collection => $"Collection({collection.Count})",
      IFormattable formattable => formattable.ToString(
          null,
          CultureInfo.InvariantCulture),
      _ => value.ToString() ?? value.GetType().Name
    };
  }

  private static string Safe(string value) {
    return string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '_'));
  }
}
