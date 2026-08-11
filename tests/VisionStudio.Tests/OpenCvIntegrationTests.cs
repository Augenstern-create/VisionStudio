using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Tests;

public sealed class OpenCvIntegrationTests
{
    [Fact]
    public async Task Executor_RunsRealImagePipeline()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "images", "input"));
        var inputPath = Path.Combine(temp.Path, "images", "input", "input.png");
        using (var input = new Mat(8, 10, DepthType.Cv8U, 3))
        {
            input.SetTo(new MCvScalar(200, 100, 20));
            Assert.True(CvInvoke.Imwrite(inputPath, input));
        }
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = WorkflowTests.LinearWorkflow(registry);
        using var result = await new WorkflowExecutor(registry, temp.Path).ExecuteAsync(workflow);
        var output = result.GetOutput<Mat>(workflow.Nodes[^1].Id, "Image");
        Assert.Equal(8, output.Rows);
        Assert.Equal(10, output.Cols);
        Assert.Equal(1, output.NumberOfChannels);
        Assert.Equal(3, result.Timings.Count);
    }

    [Fact]
    public async Task Executor_RejectsEvenGaussianKernel()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "images", "input"));
        using (var input = new Mat(4, 4, DepthType.Cv8U, 3))
        {
            input.SetTo(new MCvScalar(100));
            Assert.True(CvInvoke.Imwrite(Path.Combine(temp.Path, "images", "input", "input.png"), input));
        }
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = WorkflowTests.LinearWorkflow(registry);
        registry.TryGetDescriptor("Image.GaussianBlur", out var blurDescriptor);
        var blur = OpenCvOperatorCatalog.CreateNode(blurDescriptor, 0, 0);
        blur.SetParameter("kernelSize", 4);
        var gray = workflow.Nodes[1];
        var threshold = workflow.Nodes[2];
        workflow.Nodes.Insert(2, blur);
        workflow.Connections.RemoveAll(c => c.FromNode == gray.Id && c.ToNode == threshold.Id);
        workflow.Connections.Add(new(gray.Id, "Image", blur.Id, "Image"));
        workflow.Connections.Add(new(blur.Id, "Image", threshold.Id, "Image"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new WorkflowExecutor(registry, temp.Path).ExecuteAsync(workflow));
    }

    [Fact]
    public async Task ProjectCache_ReusesUnchangedAncestorsAndInvalidatesDownstreamParameter()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "images", "input"));
        using (var input = new Mat(6, 6, DepthType.Cv8U, 3))
        {
            input.SetTo(new MCvScalar(150, 80, 20));
            Assert.True(CvInvoke.Imwrite(Path.Combine(temp.Path, "images", "input", "input.png"), input));
        }
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = WorkflowTests.LinearWorkflow(registry);
        var target = workflow.Nodes[^1];
        var executor = new WorkflowExecutor(registry, temp.Path, new OpenCvProjectCache(temp.Path));
        using (var first = await executor.ExecuteAsync(workflow, target.Id))
            Assert.All(first.Timings, timing => Assert.False(timing.FromCache));
        using (var second = await executor.ExecuteAsync(workflow, target.Id))
            Assert.All(second.Timings, timing => Assert.True(timing.FromCache));
        workflow.Nodes[1].X += 120;
        workflow.Nodes[1].Y += 40;
        using (var moved = await executor.ExecuteAsync(workflow, target.Id))
            Assert.All(moved.Timings, timing => Assert.True(timing.FromCache));
        target.SetParameter("threshold", 140d);
        using var changed = await executor.ExecuteAsync(workflow, target.Id);
        Assert.True(changed.Timings[0].FromCache);
        Assert.True(changed.Timings[1].FromCache);
        Assert.False(changed.Timings[2].FromCache);
    }

    [Fact]
    public async Task Executor_SupportsUnicodeProjectPaths()
    {
        using var temp = new TemporaryDirectory();
        var projectRoot = Path.Combine(temp.Path, "中文视觉项目");
        Directory.CreateDirectory(Path.Combine(projectRoot, "images", "input"));
        using (var input = new Mat(5, 7, DepthType.Cv8U, 3))
        {
            input.SetTo(new MCvScalar(90, 120, 180));
            OpenCvFileCodec.Write(Path.Combine(projectRoot, "images", "input", "input.png"), input);
        }
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = WorkflowTests.LinearWorkflow(registry);
        using var result = await new WorkflowExecutor(registry, projectRoot, new OpenCvProjectCache(projectRoot))
            .ExecuteAsync(workflow, workflow.Nodes[^1].Id);
        Assert.False(result.GetOutput<Mat>(workflow.Nodes[^1].Id, "Image").IsEmpty);
        Assert.True(Directory.GetFiles(Path.Combine(projectRoot, "images", "cache"), "*.png", SearchOption.AllDirectories).Length >= 3);
    }

    [Fact]
    public async Task AutomationRunner_PublishesPreviewInsideProjectCache()
    {
        using var temp = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temp.Path, "Assistant Preview");
        using (var input = new Mat(9, 11, DepthType.Cv8U, 3))
        {
            input.SetTo(new MCvScalar(120, 80, 30));
            OpenCvFileCodec.Write(Path.Combine(temp.Path, "images", "input", "input.png"), input);
        }
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = WorkflowTests.LinearWorkflow(registry);
        var target = workflow.Nodes[^1];

        var result = await new OpenCvWorkflowAutomationRunner(registry)
            .RunAsync("angle/request", project, workflow, target.Id);

        var image = Assert.Single(result.Outputs, output => output.Type == NodeValueType.Mat);
        Assert.True(result.Success);
        Assert.StartsWith("images/cache/assistant/", image.ArtifactPath, StringComparison.Ordinal);
        Assert.True(File.Exists(ProjectPaths.ResolveInside(project.RootPath, image.ArtifactPath!)));
    }
}
