using System.Text.Json;
using VisionStudio.Core;

namespace VisionStudio.Runtimes.App;

public enum RuntimeSourceKind {
  Image,
  ImageFolder,
  Video,
  Camera,
  Simulated
}

public enum RuntimeResultRole {
  Value,
  Image,
  PrimaryImage
}

public enum RuntimeTriggerKind {
  Continuous,
  Timer,
  Manual
}

public enum RuntimeQueueMode {
  ProcessAll,
  LatestFrame,
  DropWhenBusy
}

public sealed class RuntimeSourceSettings {
  public RuntimeSourceKind Kind { get; set; } = RuntimeSourceKind.Image;
  public string Path { get; set; } = "images/input/1.png";
  public int CameraIndex { get; set; }
  public int Width { get; set; } = 640;
  public int Height { get; set; } = 480;
  public bool Loop { get; set; } = true;
}

public sealed class RuntimeExecutionSettings {
  public RuntimeTriggerKind Trigger { get; set; } = RuntimeTriggerKind.Timer;
  public int IntervalMs { get; set; } = 500;
  public RuntimeQueueMode QueueMode { get; set; } = RuntimeQueueMode.LatestFrame;
  public int QueueCapacity { get; set; } = 4;
  public bool ContinueAfterError { get; set; } = true;
}

public sealed class RuntimeResultMapping {
  public required string Name { get; init; }
  public required string NodeId { get; init; }
  public required string Port { get; init; }
  public RuntimeResultRole Role { get; init; } = RuntimeResultRole.Value;
  public string? Unit { get; init; }
}

public sealed class RuntimeProfile {
  public const int CurrentSchemaVersion = 1;
  public int SchemaVersion { get; init; } = CurrentSchemaVersion;
  public string Name { get; set; } = "默认运行配置";
  public required string InputNodeId { get; init; }
  public required string TargetNodeId { get; init; }
  public RuntimeSourceSettings Source { get; set; } = new();
  public RuntimeExecutionSettings Execution { get; set; } = new();
  public List<RuntimeResultMapping> ResultMappings { get; init; } = [];
}

public sealed class RuntimeProfileStore {
  public const string DefaultProfilePath = "runtime/profiles/default.runtime.json";

  public async Task<RuntimeProfile> LoadOrCreateAsync(
      ProjectHandle project,
      WorkflowDocument workflow,
      OperatorRegistry registry,
      CancellationToken cancellationToken = default) {
    string path = ProjectPaths.ResolveInside(project.RootPath, DefaultProfilePath);
    if (!File.Exists(path)) {
      var created = CreateDefault(project, workflow, registry);
      await SaveAsync(project, created, cancellationToken);
      return created;
    }
    string json = await File.ReadAllTextAsync(path, cancellationToken);
    var profile = JsonSerializer.Deserialize<RuntimeProfile>(
        json,
        WorkflowJson.Options) ?? throw new JsonException("Runtime profile is empty.");
    if (profile.SchemaVersion > RuntimeProfile.CurrentSchemaVersion) {
      throw new NotSupportedException(
          $"Runtime profile schema {profile.SchemaVersion} is newer than supported schema "
          + $"{RuntimeProfile.CurrentSchemaVersion}.");
    }
    return profile;
  }

  public async Task SaveAsync(
      ProjectHandle project,
      RuntimeProfile profile,
      CancellationToken cancellationToken = default) {
    string path = ProjectPaths.ResolveInside(project.RootPath, DefaultProfilePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string temporary = Path.Combine(
        Path.GetDirectoryName(path)!,
        $".{Guid.NewGuid():N}.runtime.tmp");
    try {
      await File.WriteAllTextAsync(
          temporary,
          JsonSerializer.Serialize(profile, WorkflowJson.Options),
          cancellationToken);
      File.Move(temporary, path, overwrite: true);
    } finally {
      if (File.Exists(temporary)) {
        File.Delete(temporary);
      }
    }
  }

  private static RuntimeProfile CreateDefault(
      ProjectHandle project,
      WorkflowDocument workflow,
      OperatorRegistry registry) {
    var input = workflow.Nodes.SingleOrDefault(
        node => node.Type == "Input.RuntimeFrame")
        ?? throw new InvalidDataException(
            "The project has no Input.RuntimeFrame node. Add one before opening it in Runtime.");
    var validation = WorkflowValidator.Validate(workflow, registry);
    if (!validation.IsValid) {
      throw new WorkflowValidationException(validation.Errors);
    }
    var target = validation.TopologicalOrder.Last();
    registry.TryGetDescriptor(target.Type, out var descriptor);
    string inputImage = Directory
        .EnumerateFiles(ProjectPaths.ResolveInside(project.RootPath, "images/input"))
        .Where(IsImage)
        .Select(path => Path.GetRelativePath(project.RootPath, path).Replace('\\', '/'))
        .FirstOrDefault() ?? "images/input/1.png";
    var profile = new RuntimeProfile {
      InputNodeId = input.Id,
      TargetNodeId = target.Id,
      Source = new RuntimeSourceSettings {
        Kind = File.Exists(ProjectPaths.ResolveInside(project.RootPath, inputImage))
            ? RuntimeSourceKind.Image
            : RuntimeSourceKind.Simulated,
        Path = inputImage
      }
    };
    profile.ResultMappings.AddRange(descriptor.Outputs.Select((output, index) =>
        new RuntimeResultMapping {
          Name = output.DisplayName ?? output.Name,
          NodeId = target.Id,
          Port = output.Name,
          Role = output.Type == NodeValueType.Mat
              ? index == 0
                  ? RuntimeResultRole.PrimaryImage
                  : RuntimeResultRole.Image
              : RuntimeResultRole.Value
        }));
    return profile;
  }

  private static bool IsImage(string path) {
    return Path.GetExtension(path).ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".webp";
  }
}
