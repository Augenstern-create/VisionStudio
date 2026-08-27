using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.CodeGen;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Web;

public sealed record WebProjectSummary(Guid Id, string Name, string Description, DateTimeOffset ModifiedAt);

public sealed class WebProjectContext {
  private readonly ProjectService _projects;
  private readonly SemaphoreSlim _undoGate = new(1, 1);
  private readonly Stack<WorkflowDocument> _undo = new();

  public WebProjectContext(ProjectHandle handle, WorkflowDocument workflow, ProjectService projects, OperatorRegistry registry) {
    Handle = handle;
    Registry = registry;
    _projects = projects;
    Automation = new WorkflowAutomationSession(handle, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
    Automation.WorkflowChanged += (_, change) => {
      lock (_undo) {
        _undo.Push(Clone(change.PreviousWorkflow));
        if (_undo.Count > 100) {
          var recent = _undo.Take(100).Reverse().ToArray();
          _undo.Clear();
          foreach (var item in recent) {
            _undo.Push(item);
          }
        }
      }
    };
  }

  public ProjectHandle Handle { get; }
  public OperatorRegistry Registry { get; }
  public WorkflowAutomationSession Automation { get; }

  public async Task<WorkflowSnapshot> UndoAsync(CancellationToken cancellationToken) {
    await _undoGate.WaitAsync(cancellationToken);
    try {
      WorkflowDocument previous;
      lock (_undo) {
        if (_undo.Count == 0) {
          throw new InvalidOperationException("There is no operation to undo.");
        }

        previous = _undo.Pop();
      }
      await _projects.SaveWorkflowAsync(Handle, previous, createSnapshot: true, cancellationToken);
      Automation.SynchronizeFromHost(previous);
      await _projects.AppendOperationAsync(Handle, new(DateTimeOffset.UtcNow, "Web 撤销", "撤销上一项工作流修改"), cancellationToken);
      return await Automation.GetSnapshotAsync(cancellationToken);
    } finally { _undoGate.Release(); }
  }

  private static WorkflowDocument Clone(WorkflowDocument workflow) {
    return WorkflowJson.Deserialize(WorkflowJson.Serialize(workflow));
  }
}

public sealed class WebProjectStore {
  public const long MaximumUploadBytes = 50L * 1024 * 1024;
  private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
      { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" };
  private readonly ProjectService _projects = new();
  private readonly ConcurrentDictionary<Guid, Lazy<Task<WebProjectContext>>> _contexts = new();
  private readonly ILogger<WebProjectStore> _logger;
  private readonly Func<Guid, CancellationToken, Task<WebProjectContext>>? _contextLoader;

  public WebProjectStore(IConfiguration configuration, ILogger<WebProjectStore> logger)
      : this(configuration["VisionStudio:DataRoot"] ?? Path.Combine(AppContext.BaseDirectory, "data"), logger) { }

  public WebProjectStore(string root, ILogger<WebProjectStore>? logger = null)
      : this(root, logger, null) { }

  internal WebProjectStore(string root, ILogger<WebProjectStore>? logger,
      Func<Guid, CancellationToken, Task<WebProjectContext>>? contextLoader) {
    RootPath = Path.GetFullPath(root);
    _logger = logger ?? NullLogger<WebProjectStore>.Instance;
    _contextLoader = contextLoader;
    Directory.CreateDirectory(RootPath);
  }

  public string RootPath { get; }

  public async Task<IReadOnlyList<WebProjectSummary>> ListAsync(CancellationToken cancellationToken) {
    var results = new List<WebProjectSummary>();
    foreach (string directory in Directory.EnumerateDirectories(RootPath)) {
      cancellationToken.ThrowIfCancellationRequested();
      try {
        var handle = await _projects.OpenAsync(directory, cancellationToken);
        results.Add(new(handle.Project.Id, handle.Project.Name, handle.Project.Description, handle.Project.ModifiedAt));
      } catch (Exception exception) when (IsInvalidProjectException(exception)) {
        LogInvalidProject(directory, exception);
      }
    }
    return [.. results.OrderByDescending(item => item.ModifiedAt)];
  }

  public async Task<WebProjectContext> CreateAsync(string name, CancellationToken cancellationToken) {
    name = NormalizeName(name);
    string folderName = SafeFileName(name) + "-" + Guid.NewGuid().ToString("N")[..8];
    string root = ProjectPaths.ResolveInside(RootPath, folderName);
    var handle = await _projects.CreateAsync(root, name, cancellationToken);
    var workflow = await _projects.LoadWorkflowAsync(handle, cancellationToken);
    var context = CreateContext(handle, workflow);
    _contexts[handle.Project.Id] = new(Task.FromResult(context));
    return context;
  }

  public Task<WebProjectContext> GetAsync(Guid id, CancellationToken cancellationToken) {
    var lazy = _contexts.GetOrAdd(id, key => new(
        () => (_contextLoader ?? LoadAsync)(key, CancellationToken.None),
        LazyThreadSafetyMode.ExecutionAndPublication));
    return AwaitAndRepairAsync(id, lazy, cancellationToken);
  }

  public async Task<string> SaveImageAsync(WebProjectContext context, IFormFile file, CancellationToken cancellationToken) {
    if (file.Length <= 0 || file.Length > MaximumUploadBytes) {
      throw new InvalidDataException($"Image size must be between 1 byte and {MaximumUploadBytes / 1024 / 1024} MB.");
    }

    string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!ImageExtensions.Contains(extension)) {
      throw new InvalidDataException("Unsupported image format.");
    }

    string stem = SafeFileName(Path.GetFileNameWithoutExtension(file.FileName));
    string relative = $"images/input/{stem}-{Guid.NewGuid():N}{extension}";
    string destination = ProjectPaths.ResolveInside(context.Handle.RootPath, relative);
    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous)) {
      await file.CopyToAsync(output, cancellationToken);
    }

    try {
      using var image = OpenCvFileCodec.Read(destination);
      if (image.IsEmpty) {
        throw new InvalidDataException("OpenCV could not decode the uploaded image.");
      }
    } catch {
      File.Delete(destination);
      throw;
    }
    return relative;
  }

  public async Task<string> ExportAsync(WebProjectContext context, string? targetNodeId, CancellationToken cancellationToken) {
    var snapshot = await context.Automation.GetSnapshotAsync(cancellationToken);
    string code = new CSharpWorkflowGenerator(context.Registry).Generate(snapshot.Workflow, targetNodeId: targetNodeId);
    string path = ProjectPaths.ResolveInside(context.Handle.RootPath, "output/GeneratedVisionAlgorithm.cs");
    await File.WriteAllTextAsync(path, code, cancellationToken);
    return path;
  }

  public Task<IReadOnlyList<ProjectOperation>> OperationsAsync(WebProjectContext context, CancellationToken cancellationToken) {
    return _projects.LoadOperationsAsync(context.Handle, 200, cancellationToken);
  }

  public string ResolveReadableFile(WebProjectContext context, string relativePath) {
    string normalized = relativePath.Replace('\\', '/').TrimStart('/');
    bool allowed = normalized.StartsWith("images/input/", StringComparison.Ordinal) ||
                  normalized.StartsWith("images/cache/", StringComparison.Ordinal) ||
                  normalized.StartsWith("images/output/", StringComparison.Ordinal) ||
                  normalized.StartsWith("output/", StringComparison.Ordinal);
    if (!allowed) {
      throw new InvalidOperationException("The requested project file is not web-readable.");
    }

    string path = ProjectPaths.ResolveInside(context.Handle.RootPath, normalized);
    if (!File.Exists(path)) {
      throw new FileNotFoundException("Project artifact does not exist.", path);
    }

    return path;
  }

  private async Task<WebProjectContext> LoadAsync(Guid id, CancellationToken cancellationToken) {
    foreach (string directory in Directory.EnumerateDirectories(RootPath)) {
      cancellationToken.ThrowIfCancellationRequested();
      ProjectHandle handle;
      try {
        handle = await _projects.OpenAsync(directory, cancellationToken);
      } catch (Exception exception) when (IsInvalidProjectException(exception)) {
        LogInvalidProject(directory, exception);
        continue;
      }
      if (handle.Project.Id != id) {
        continue;
      }

      return CreateContext(handle, await _projects.LoadWorkflowAsync(handle, cancellationToken));
    }
    throw new KeyNotFoundException($"Project '{id}' does not exist.");
  }

  private async Task<WebProjectContext> AwaitAndRepairAsync(Guid id, Lazy<Task<WebProjectContext>> lazy,
      CancellationToken cancellationToken) {
    var sharedLoad = lazy.Value;
    try {
      return await sharedLoad.WaitAsync(cancellationToken);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !sharedLoad.IsCompleted) {
      throw;
    } catch {
      if (sharedLoad.IsCompleted) {
        _contexts.TryRemove(new(id, lazy));
      }
      throw;
    }
  }

  private WebProjectContext CreateContext(ProjectHandle handle, WorkflowDocument workflow) {
    var registry = OpenCvOperatorCatalog.CreateRegistry();
    return new(handle, workflow, _projects, registry);
  }

  private static bool IsInvalidProjectException(Exception exception) {
    return exception is IOException or JsonException or InvalidDataException or InvalidOperationException;
  }

  private void LogInvalidProject(string directory, Exception exception) {
    _logger.LogWarning(exception, "Skipping unreadable Vision Studio project directory {ProjectDirectory}.",
        Path.GetFileName(directory));
  }

  private static string NormalizeName(string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    name = name.Trim();
    if (name.Length > 80) {
      throw new ArgumentOutOfRangeException(nameof(name), "Project name must not exceed 80 characters.");
    }

    return name;
  }

  private static string SafeFileName(string value) {
    string result = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\' ? '_' : character)).Trim();
    return string.IsNullOrWhiteSpace(result) ? "VisionProject" : result;
  }
}
