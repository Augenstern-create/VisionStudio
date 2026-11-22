using System.Text;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Tests;

public sealed class BatchRunnerTests
{
    [Fact]
    public async Task Executor_ReportsPerNodeProgress()
    {
        using var temp = new TemporaryDirectory();
        string input = Path.Combine(temp.Path, "images", "input");
        Directory.CreateDirectory(input);
        File.Copy(FindRepositoryFile("tests", "image", "1.png"), Path.Combine(input, "1.png"));
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var load = new WorkflowNode { Id = "load", Type = "Image.Load", Name = "加载图像" };
        load.SetParameter("path", "images/input/1.png");
        var workflow = new WorkflowDocument
        {
            Nodes = { load, new() { Id = "gray", Type = "Image.Gray", Name = "灰度化" } },
            Connections = { new WorkflowConnection("load", "Image", "gray", "Image") }
        };
        var reports = new List<WorkflowProgress>();
        var executor = new WorkflowExecutor(registry, temp.Path) {
            Progress = new Collector<WorkflowProgress>(reports.Add)
        };

        using var result = await executor.ExecuteAsync(workflow, "gray");

        Assert.Equal(2, reports.Count);
        Assert.Equal([1, 2], reports.Select(item => item.Index));
        Assert.All(reports, item => Assert.Equal(2, item.Total));
        Assert.Contains(reports, item => item.NodeId == "gray" && !item.FromCache);
    }

    [Fact]
    public async Task BatchRunner_RunsEveryImageAndKeepsOutputsPerFile()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(source);
        for (int index = 1; index <= 3; index++)
        {
            File.Copy(FindRepositoryFile("tests", "image", "1.png"), Path.Combine(source, $"{index}.png"));
        }

        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument
        {
            Nodes =
            {
                new() { Id = "load", Type = "Image.Load", Name = "加载图像" },
                new() { Id = "gray", Type = "Image.Gray", Name = "灰度化" }
            },
            Connections = { new WorkflowConnection("load", "Image", "gray", "Image") }
        };
        var runner = new BatchWorkflowRunner(registry, temp.Path);
        var files = BatchWorkflowRunner.CollectImages(source);

        var results = await runner.RunAsync(workflow, "load", "gray", files);

        Assert.Equal(3, results.Count);
        Assert.All(results, item => Assert.True(item.Success, item.Error));
        Assert.All(results, item => Assert.Contains(item.Outputs, output => output.Port == "Image" && output.Summary.Contains("1 channel")));
        Assert.Equal(["1.png", "2.png", "3.png"], results.Select(item => item.File));
    }

    [Fact]
    public async Task BatchRunner_RecordsDecodeFailureAndContinues()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(source);
        File.Copy(FindRepositoryFile("tests", "image", "1.png"), Path.Combine(source, "1-good.png"));
        await File.WriteAllTextAsync(Path.Combine(source, "2-broken.png"), "not an image");

        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument
        {
            Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } }
        };
        var runner = new BatchWorkflowRunner(registry, temp.Path);

        var results = await runner.RunAsync(workflow, "load", "load", BatchWorkflowRunner.CollectImages(source));

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Success, results[0].Error);
        Assert.False(results[1].Success);
        Assert.NotNull(results[1].Error);
    }

    [Fact]
    public async Task BatchRunner_HonoursCancellation()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "images");
        Directory.CreateDirectory(source);
        for (int index = 0; index < 4; index++)
        {
            File.Copy(FindRepositoryFile("tests", "image", "1.png"), Path.Combine(source, $"{index}.png"));
        }

        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument { Nodes = { new() { Id = "load", Type = "Image.Load", Name = "加载图像" } } };
        var runner = new BatchWorkflowRunner(registry, temp.Path);
        using var cancellation = new CancellationTokenSource();
        var seen = 0;
        var progress = new Collector<BatchItemResult>(_ => {
            if (Interlocked.Increment(ref seen) == 1) {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(workflow, "load", "load", BatchWorkflowRunner.CollectImages(source), progress, cancellation.Token));
    }

    [Fact]
    public async Task BatchCsv_WritesBomEscapedRowsAndEmptyCells()
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "batch.csv");
        var results = new List<BatchItemResult>
        {
            new("a.png", true, TimeSpan.FromMilliseconds(42), [new("Count", "3"), new("PixelLength", "128.4")], null),
            new("b.png", false, TimeSpan.FromMilliseconds(7), [], "无法解码, 已跳过")
        };

        await BatchResultCsv.WriteAsync(path, ["Count", "PixelLength"], results);

        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        string text = Encoding.UTF8.GetString(bytes[3..]);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Equal("file,success,duration_ms,error,Count,PixelLength", lines[0]);
        Assert.Equal("a.png,true,42,,3,128.4", lines[1]);
        Assert.Equal("b.png,false,7,\"无法解码, 已跳过\",,", lines[2]);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }

    private sealed class Collector<T>(Action<T>? onReport = null) : IProgress<T>
    {
        public void Report(T value) => onReport?.Invoke(value);
    }
}
