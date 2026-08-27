using Emgu.CV;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Tests;

public sealed class CrackMeasurementTests
{
    [Fact]
    public async Task CrackMeasurement_MeasuresProvidedImageAndProducesOverlay()
    {
        using var temp = new TemporaryDirectory();
        var inputDirectory = Path.Combine(temp.Path, "images", "input");
        Directory.CreateDirectory(inputDirectory);
        File.Copy(FindRepositoryFile("tests", "image", "1.png"), Path.Combine(inputDirectory, "1.png"));

        var registry = OpenCvOperatorCatalog.CreateRegistry();
        registry.TryGetDescriptor("Image.Load", out var loadDescriptor);
        registry.TryGetDescriptor("Defect.CrackMeasure", out var crackDescriptor);
        var load = OpenCvOperatorCatalog.CreateNode(loadDescriptor, 0, 0);
        load.SetParameter("path", "images/input/1.png");
        var crack = OpenCvOperatorCatalog.CreateNode(crackDescriptor, 220, 0);
        var workflow = new WorkflowDocument
        {
            Nodes = { load, crack },
            Connections = { new(load.Id, "Image", crack.Id, "Image") }
        };

        using var result = await new WorkflowExecutor(registry, temp.Path).ExecuteAsync(workflow, crack.Id);
        var count = result.GetOutput<int>(crack.Id, "CrackCount");
        var length = result.GetOutput<double>(crack.Id, "PixelLength");
        var summary = result.GetOutput<string>(crack.Id, "Summary");
        var overlay = result.GetOutput<Mat>(crack.Id, "Image");

        Assert.Equal(1, count);
        Assert.InRange(length, 450, 700);
        Assert.False(overlay.IsEmpty);
        Assert.Contains("裂纹", summary);
        var artifactDirectory = Path.Combine(FindRepositoryRoot(), "artifacts", "ai-tests");
        Directory.CreateDirectory(artifactDirectory);
        var artifact = Path.Combine(artifactDirectory, "crack-measurement-1.png");
        OpenCvFileCodec.Write(artifact, overlay);
        Console.WriteLine($"CrackCount={count}; PixelLength={length:F1}; Overlay={artifact}");
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Repository test file was not found: {Path.Combine(parts)}");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "VisionStudio.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
