using System.Text.Json;

namespace VisionStudio.Core;

public sealed class VisionProject
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public string MainWorkflow { get; set; } = "workflows/main.vflow";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Tags { get; init; } = [];
}

public sealed record ProjectHandle(string RootPath, string ProjectFilePath, VisionProject Project)
{
    public string MainWorkflowPath => ProjectPaths.ResolveInside(RootPath, Project.MainWorkflow);
}

public static class ProjectPaths
{
    public static string ResolveInside(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException("Project paths must be relative.");
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path '{relativePath}' escapes the project root.");
        return candidate;
    }
}

public sealed class ProjectService
{
    public const string ProjectExtension = ".vstudio";
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private static readonly JsonSerializerOptions CompactJson = new(WorkflowJson.Options) { WriteIndented = false };
    private static readonly string[] Directories =
    [
        "workflows", "images/input", "images/cache", "images/output", "models", "resources",
        "snapshots", "generated", "output", ".project/autosave"
    ];

    public async Task<ProjectHandle> CreateAsync(string rootPath, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var root = Path.GetFullPath(rootPath);
        if (Directory.Exists(root) && Directory.EnumerateFiles(root, $"*{ProjectExtension}").Any())
            throw new InvalidOperationException("A project already exists at this path.");
        Directory.CreateDirectory(root);
        foreach (var directory in Directories) Directory.CreateDirectory(ProjectPaths.ResolveInside(root, directory));
        var project = new VisionProject { Name = name };
        var handle = new ProjectHandle(root, GetProjectFilePath(root, name), project);
        await SaveProjectAsync(handle, cancellationToken);
        await SaveWorkflowAsync(handle, new WorkflowDocument(), createSnapshot: false, cancellationToken);
        return handle;
    }

    public async Task<ProjectHandle> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        string root;
        string projectFile;
        if (File.Exists(fullPath))
        {
            if (!string.Equals(Path.GetExtension(fullPath), ProjectExtension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsupported project file '{Path.GetFileName(fullPath)}'.");
            root = Path.GetDirectoryName(fullPath)!;
            projectFile = fullPath;
        }
        else if (Directory.Exists(fullPath))
        {
            root = fullPath;
            var projectFiles = Directory.GetFiles(root, $"*{ProjectExtension}", SearchOption.TopDirectoryOnly);
            if (projectFiles.Length > 1) throw new InvalidOperationException("The folder contains more than one Vision Studio project file.");
            projectFile = projectFiles.SingleOrDefault()
                ?? throw new FileNotFoundException($"The folder does not contain a {ProjectExtension} project file.", root);
        }
        else
        {
            throw new FileNotFoundException("The project file or folder does not exist.", fullPath);
        }

        var json = await File.ReadAllTextAsync(projectFile, cancellationToken);
        var project = JsonSerializer.Deserialize<VisionProject>(json, WorkflowJson.Options)
            ?? throw new JsonException("Project content is empty.");
        if (project.SchemaVersion > 1) throw new NotSupportedException($"Project schema {project.SchemaVersion} is newer than supported schema 1.");
        foreach (var directory in Directories) Directory.CreateDirectory(ProjectPaths.ResolveInside(root, directory));
        _ = ProjectPaths.ResolveInside(root, project.MainWorkflow);
        return new ProjectHandle(root, projectFile, project);
    }

    public async Task<WorkflowDocument> LoadWorkflowAsync(ProjectHandle handle, CancellationToken cancellationToken = default) =>
        WorkflowJson.Deserialize(await File.ReadAllTextAsync(handle.MainWorkflowPath, cancellationToken));

    public async Task SaveWorkflowAsync(ProjectHandle handle, WorkflowDocument workflow, bool createSnapshot = true, CancellationToken cancellationToken = default)
    {
        if (createSnapshot && File.Exists(handle.MainWorkflowPath))
        {
            var snapshot = ProjectPaths.ResolveInside(handle.RootPath, $"snapshots/{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-main.vflow");
            File.Copy(handle.MainWorkflowPath, snapshot, overwrite: false);
        }
        await AtomicWriteAsync(handle.MainWorkflowPath, WorkflowJson.Serialize(workflow), cancellationToken);
        var autosave = ProjectPaths.ResolveInside(handle.RootPath, ".project/autosave/main.vflow");
        await AtomicWriteAsync(autosave, WorkflowJson.Serialize(workflow), cancellationToken);
        handle.Project.ModifiedAt = DateTimeOffset.UtcNow;
        await SaveProjectAsync(handle, cancellationToken);
    }

    public Task SaveAutosaveAsync(ProjectHandle handle, WorkflowDocument workflow, CancellationToken cancellationToken = default)
    {
        var autosave = ProjectPaths.ResolveInside(handle.RootPath, ".project/autosave/main.vflow");
        return AtomicWriteAsync(autosave, WorkflowJson.Serialize(workflow), cancellationToken);
    }

    public async Task<string> ImportInputImageAsync(ProjectHandle handle, string sourcePath, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Input image does not exist.", source);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".webp"))
            throw new InvalidDataException($"Unsupported image extension '{extension}'.");
        var stem = string.Concat(Path.GetFileNameWithoutExtension(source).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var destinationName = stem + extension;
        var relative = Path.Combine("images", "input", destinationName).Replace('\\', '/');
        var destination = ProjectPaths.ResolveInside(handle.RootPath, relative);
        if (File.Exists(destination) && !FilesEqual(source, destination))
        {
            destinationName = $"{stem}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}";
            relative = Path.Combine("images", "input", destinationName).Replace('\\', '/');
            destination = ProjectPaths.ResolveInside(handle.RootPath, relative);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            await using var input = File.OpenRead(source);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
        return relative;
    }

    public async Task AppendOperationAsync(ProjectHandle handle, ProjectOperation operation, CancellationToken cancellationToken = default)
    {
        var path = ProjectPaths.ResolveInside(handle.RootPath, ".project/operations.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(operation, CompactJson) + Environment.NewLine;
        await _operationGate.WaitAsync(cancellationToken);
        try { await File.AppendAllTextAsync(path, line, cancellationToken); }
        finally { _operationGate.Release(); }
    }

    public async Task<IReadOnlyList<ProjectOperation>> LoadOperationsAsync(ProjectHandle handle, int maximum = 200, CancellationToken cancellationToken = default)
    {
        var path = ProjectPaths.ResolveInside(handle.RootPath, ".project/operations.jsonl");
        if (!File.Exists(path)) return [];
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var result = new List<ProjectOperation>();
        foreach (var line in lines.TakeLast(maximum))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var operation = JsonSerializer.Deserialize<ProjectOperation>(line, CompactJson);
                if (operation is not null) result.Add(operation);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static bool FilesEqual(string first, string second)
    {
        var a = new FileInfo(first); var b = new FileInfo(second);
        if (a.Length != b.Length) return false;
        using var left = File.OpenRead(first); using var right = File.OpenRead(second);
        int x;
        while ((x = left.ReadByte()) != -1) if (x != right.ReadByte()) return false;
        return right.ReadByte() == -1;
    }

    private static Task SaveProjectAsync(ProjectHandle handle, CancellationToken cancellationToken) =>
        AtomicWriteAsync(handle.ProjectFilePath, JsonSerializer.Serialize(handle.Project, WorkflowJson.Options), cancellationToken);

    private static string GetProjectFilePath(string root, string name)
    {
        var safeName = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "VisionProject";
        return Path.Combine(root, safeName + ProjectExtension);
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed record ProjectOperation(DateTimeOffset Timestamp, string Action, string? Detail = null);

public sealed record RecentProject(Guid Id, string Name, string RootPath, DateTimeOffset LastOpenedAt);

public sealed class RecentProjectCatalog(string? catalogPath = null)
{
    private readonly string _catalogPath = catalogPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", "recent-projects.json");

    public async Task<IReadOnlyList<RecentProject>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_catalogPath)) return [];
        var items = JsonSerializer.Deserialize<List<RecentProject>>(await File.ReadAllTextAsync(_catalogPath, cancellationToken), WorkflowJson.Options) ?? [];
        return items.Where(x => Directory.Exists(x.RootPath)).OrderByDescending(x => x.LastOpenedAt).ToList();
    }

    public async Task RecordAsync(ProjectHandle handle, CancellationToken cancellationToken = default)
    {
        var items = (await ListAsync(cancellationToken)).Where(x => x.Id != handle.Project.Id).ToList();
        items.Insert(0, new(handle.Project.Id, handle.Project.Name, handle.RootPath, DateTimeOffset.UtcNow));
        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        await File.WriteAllTextAsync(_catalogPath, JsonSerializer.Serialize(items.Take(30), WorkflowJson.Options), cancellationToken);
    }
}
