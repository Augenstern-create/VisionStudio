using System.Collections.Concurrent;
using System.Text.Json;
using VisionStudio.CodeGen;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Web;

public sealed record WebProjectSummary(Guid Id, string Name, string Description, DateTimeOffset ModifiedAt);

public sealed class WebProjectContext
{
    private readonly ProjectService _projects;
    private readonly SemaphoreSlim _undoGate = new(1, 1);
    private readonly Stack<WorkflowDocument> _undo = new();

    public WebProjectContext(ProjectHandle handle, WorkflowDocument workflow, ProjectService projects, OperatorRegistry registry)
    {
        Handle = handle;
        Registry = registry;
        _projects = projects;
        Automation = new WorkflowAutomationSession(handle, workflow, projects, registry, new OpenCvWorkflowAutomationRunner(registry));
        Automation.WorkflowChanged += (_, change) =>
        {
            lock (_undo)
            {
                _undo.Push(Clone(change.PreviousWorkflow));
                if (_undo.Count > 100)
                {
                    var recent = _undo.Take(100).Reverse().ToArray();
                    _undo.Clear();
                    foreach (var item in recent) _undo.Push(item);
                }
            }
        };
    }

    public ProjectHandle Handle { get; }
    public OperatorRegistry Registry { get; }
    public WorkflowAutomationSession Automation { get; }

    public async Task<WorkflowSnapshot> UndoAsync(CancellationToken cancellationToken)
    {
        await _undoGate.WaitAsync(cancellationToken);
        try
        {
            WorkflowDocument previous;
            lock (_undo)
            {
                if (_undo.Count == 0) throw new InvalidOperationException("There is no operation to undo.");
                previous = _undo.Pop();
            }
            await _projects.SaveWorkflowAsync(Handle, previous, createSnapshot: true, cancellationToken);
            Automation.SynchronizeFromHost(previous);
            await _projects.AppendOperationAsync(Handle, new(DateTimeOffset.UtcNow, "Web 撤销", "撤销上一项工作流修改"), cancellationToken);
            return await Automation.GetSnapshotAsync(cancellationToken);
        }
        finally { _undoGate.Release(); }
    }

    private static WorkflowDocument Clone(WorkflowDocument workflow) => WorkflowJson.Deserialize(WorkflowJson.Serialize(workflow));
}

public sealed class WebProjectStore
{
    public const long MaximumUploadBytes = 50L * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" };
    private readonly string _root;
    private readonly ProjectService _projects = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<WebProjectContext>>> _contexts = new();

    public WebProjectStore(IConfiguration configuration)
        : this(configuration["VisionStudio:DataRoot"] ?? Path.Combine(AppContext.BaseDirectory, "data")) { }

    public WebProjectStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string RootPath => _root;

    public async Task<IReadOnlyList<WebProjectSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var results = new List<WebProjectSummary>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var handle = await _projects.OpenAsync(directory, cancellationToken);
                results.Add(new(handle.Project.Id, handle.Project.Name, handle.Project.Description, handle.Project.ModifiedAt));
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or InvalidOperationException) { }
        }
        return results.OrderByDescending(item => item.ModifiedAt).ToList();
    }

    public async Task<WebProjectContext> CreateAsync(string name, CancellationToken cancellationToken)
    {
        name = NormalizeName(name);
        var folderName = SafeFileName(name) + "-" + Guid.NewGuid().ToString("N")[..8];
        var root = ProjectPaths.ResolveInside(_root, folderName);
        var handle = await _projects.CreateAsync(root, name, cancellationToken);
        var workflow = await _projects.LoadWorkflowAsync(handle, cancellationToken);
        var context = CreateContext(handle, workflow);
        _contexts[handle.Project.Id] = new(Task.FromResult(context));
        return context;
    }

    public Task<WebProjectContext> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var lazy = _contexts.GetOrAdd(id, key => new(() => LoadAsync(key, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitAndRepairAsync(id, lazy);
    }

    public async Task<string> SaveImageAsync(WebProjectContext context, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length <= 0 || file.Length > MaximumUploadBytes)
            throw new InvalidDataException($"Image size must be between 1 byte and {MaximumUploadBytes / 1024 / 1024} MB.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!ImageExtensions.Contains(extension)) throw new InvalidDataException("Unsupported image format.");
        var stem = SafeFileName(Path.GetFileNameWithoutExtension(file.FileName));
        var relative = $"images/input/{stem}-{Guid.NewGuid():N}{extension}";
        var destination = ProjectPaths.ResolveInside(context.Handle.RootPath, relative);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await file.CopyToAsync(output, cancellationToken);
        try
        {
            using var image = OpenCvFileCodec.Read(destination);
            if (image.IsEmpty) throw new InvalidDataException("OpenCV could not decode the uploaded image.");
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
        return relative;
    }

    public async Task<string> ExportAsync(WebProjectContext context, CancellationToken cancellationToken)
    {
        var snapshot = await context.Automation.GetSnapshotAsync(cancellationToken);
        var code = new CSharpWorkflowGenerator(context.Registry).Generate(snapshot.Workflow);
        var path = ProjectPaths.ResolveInside(context.Handle.RootPath, "output/GeneratedVisionAlgorithm.cs");
        await File.WriteAllTextAsync(path, code, cancellationToken);
        return path;
    }

    public Task<IReadOnlyList<ProjectOperation>> OperationsAsync(WebProjectContext context, CancellationToken cancellationToken) =>
        _projects.LoadOperationsAsync(context.Handle, 200, cancellationToken);

    public string ResolveReadableFile(WebProjectContext context, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var allowed = normalized.StartsWith("images/input/", StringComparison.Ordinal) ||
                      normalized.StartsWith("images/cache/", StringComparison.Ordinal) ||
                      normalized.StartsWith("images/output/", StringComparison.Ordinal) ||
                      normalized.StartsWith("output/", StringComparison.Ordinal);
        if (!allowed) throw new InvalidOperationException("The requested project file is not web-readable.");
        var path = ProjectPaths.ResolveInside(context.Handle.RootPath, normalized);
        if (!File.Exists(path)) throw new FileNotFoundException("Project artifact does not exist.", path);
        return path;
    }

    private async Task<WebProjectContext> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectHandle handle;
            try { handle = await _projects.OpenAsync(directory, cancellationToken); }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or InvalidOperationException) { continue; }
            if (handle.Project.Id != id) continue;
            return CreateContext(handle, await _projects.LoadWorkflowAsync(handle, cancellationToken));
        }
        throw new KeyNotFoundException($"Project '{id}' does not exist.");
    }

    private async Task<WebProjectContext> AwaitAndRepairAsync(Guid id, Lazy<Task<WebProjectContext>> lazy)
    {
        try { return await lazy.Value; }
        catch { _contexts.TryRemove(new(id, lazy)); throw; }
    }

    private WebProjectContext CreateContext(ProjectHandle handle, WorkflowDocument workflow)
    {
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        return new(handle, workflow, _projects, registry);
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.Length > 80) throw new ArgumentOutOfRangeException(nameof(name), "Project name must not exceed 80 characters.");
        return name;
    }

    private static string SafeFileName(string value)
    {
        var result = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\' ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(result) ? "VisionProject" : result;
    }
}
