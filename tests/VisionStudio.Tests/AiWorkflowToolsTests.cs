using VisionStudio.Ai.Abstractions;

using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Tests;

public sealed class AiWorkflowToolsTests
{
    [Fact]
    public async Task Sweep_RunsEveryValueAndReportsTheTargetOutputs()
    {
        using var temp = new TemporaryDirectory();
        var (registry, workflow) = CreateWorkflow(temp.Path);
        var tools = new OpenCvAiWorkflowTools(registry, temp.Path, () => workflow);

        string report = await tools.SweepParameterAsync(
            new AiParameterSweep("blur", "kernelSize", ["3", "5", "7"], TargetNodeId: "gray"));

        string[] lines = report.Split('\n');
        Assert.StartsWith("swept blur.kernelSize over 3 values", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.Contains("kernelSize=3", report);
        Assert.Contains("kernelSize=7", report);
        Assert.All(lines[1..], line => Assert.Contains("ms,", line));
        Assert.All(lines[1..], line => Assert.Contains("Image=", line));
    }

    [Fact]
    public async Task Sweep_RejectsUnknownParameterAndListsValidOnes()
    {
        using var temp = new TemporaryDirectory();
        var (registry, workflow) = CreateWorkflow(temp.Path);
        var tools = new OpenCvAiWorkflowTools(registry, temp.Path, () => workflow);

        string report = await tools.SweepParameterAsync(
            new AiParameterSweep("blur", "kernel", ["3", "5"]));

        Assert.StartsWith("error:", report);
        Assert.Contains("kernelSize", report);
    }

    [Fact]
    public async Task Execute_UsesACloneSoTheCanvasWorkflowIsUntouched()
    {
        using var temp = new TemporaryDirectory();
        var (registry, workflow) = CreateWorkflow(temp.Path);
        var tools = new OpenCvAiWorkflowTools(registry, temp.Path, () => workflow);

        string report = await tools.ExecuteNodeAsync("gray");

        Assert.Contains("ms,", report);
        Assert.Contains("Image=", report);
        Assert.DoesNotContain("sweepMarker", workflow.Nodes.First(node => node.Id == "blur").Parameters.Keys);
    }

    private static (OperatorRegistry Registry, WorkflowDocument Workflow) CreateWorkflow(string root) {
        string input = Path.Combine(root, "images", "input");
        Directory.CreateDirectory(input);
        File.Copy(FindRepositoryFile("tests", "image", "1.png"), Path.Combine(input, "1.png"));
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var load = new WorkflowNode { Id = "load", Type = "Image.Load", Name = "加载图像" };
        load.SetParameter("path", "images/input/1.png");
        var blur = new WorkflowNode { Id = "blur", Type = "Image.GaussianBlur", Name = "高斯模糊" };
        blur.SetParameter("kernelSize", 3);
        blur.SetParameter("sigma", 1.5);
        var gray = new WorkflowNode { Id = "gray", Type = "Image.Gray", Name = "灰度化" };
        var workflow = new WorkflowDocument {
            Nodes = { load, blur, gray },
            Connections = {
                new WorkflowConnection("load", "Image", "blur", "Image"),
                new WorkflowConnection("blur", "Image", "gray", "Image")
            }
        };
        return (registry, workflow);
    }

    private static string FindRepositoryFile(params string[] parts) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }
}
