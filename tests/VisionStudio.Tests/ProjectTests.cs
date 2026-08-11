using VisionStudio.Core;

namespace VisionStudio.Tests;

public sealed class ProjectTests
{
    [Fact]
    public async Task CreateSaveOpen_CreatesExpectedLayoutAndSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var service = new ProjectService();
        var handle = await service.CreateAsync(temp.Path, "Gear");
        Assert.Equal(".vstudio", Path.GetExtension(handle.ProjectFilePath));
        Assert.True(File.Exists(handle.ProjectFilePath));
        Assert.True(File.Exists(handle.MainWorkflowPath));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "images", "input")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "images", "cache")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "images", "output")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "output")));

        var workflow = new WorkflowDocument { Name = "Changed" };
        await service.SaveWorkflowAsync(handle, workflow);
        var reopened = await service.OpenAsync(handle.ProjectFilePath);
        Assert.Equal("Changed", (await service.LoadWorkflowAsync(reopened)).Name);
        Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, "snapshots"), "*.vflow"));
        Assert.True(File.Exists(Path.Combine(temp.Path, ".project", "autosave", "main.vflow")));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("C:/outside.png")]
    public void ResolveInside_RejectsEscapingPath(string path)
    {
        using var temp = new TemporaryDirectory();
        Assert.Throws<InvalidOperationException>(() => ProjectPaths.ResolveInside(temp.Path, path));
    }

    [Fact]
    public async Task RecentCatalog_DeduplicatesProjects()
    {
        using var temp = new TemporaryDirectory();
        var projectRoot = Path.Combine(temp.Path, "project");
        var handle = await new ProjectService().CreateAsync(projectRoot, "Demo");
        var catalog = new RecentProjectCatalog(Path.Combine(temp.Path, "recent.json"));
        await catalog.RecordAsync(handle);
        await catalog.RecordAsync(handle);
        Assert.Single(await catalog.ListAsync());
    }

    [Fact]
    public async Task ImportInputImage_CopiesIntoProjectAndAvoidsTraversal()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "outside.png");
        await File.WriteAllBytesAsync(source, [137, 80, 78, 71]);
        var projectRoot = Path.Combine(temp.Path, "project");
        var service = new ProjectService();
        var handle = await service.CreateAsync(projectRoot, "Demo");
        var relative = await service.ImportInputImageAsync(handle, source);
        Assert.Equal("images/input/outside.png", relative);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(ProjectPaths.ResolveInside(projectRoot, relative)));
    }

    [Fact]
    public async Task OperationLog_AppendsAndLoadsInOrder()
    {
        using var temp = new TemporaryDirectory();
        var service = new ProjectService();
        var handle = await service.CreateAsync(temp.Path, "History");
        var first = new ProjectOperation(DateTimeOffset.UtcNow, "添加算子", "灰度化");
        var second = new ProjectOperation(DateTimeOffset.UtcNow.AddSeconds(1), "撤销", "添加算子");
        await service.AppendOperationAsync(handle, first);
        await service.AppendOperationAsync(handle, second);
        Assert.Equal([first, second], await service.LoadOperationsAsync(handle));
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VisionStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public string Path { get; }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
