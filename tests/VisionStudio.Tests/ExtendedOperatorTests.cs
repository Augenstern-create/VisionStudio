using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using VisionStudio.Core;
using VisionStudio.CodeGen;
using VisionStudio.Operators.OpenCv;
using System.Text.RegularExpressions;

namespace VisionStudio.Tests;

public sealed class ExtendedOperatorTests
{
    private readonly OperatorRegistry _registry = OpenCvOperatorCatalog.CreateRegistry();

    [Fact]
    public void Catalog_ContainsRequestedCategoriesAndMultiPortOperators()
    {
        Assert.True(_registry.Descriptors.Count >= 100, $"Only {_registry.Descriptors.Count} operators were registered.");
        var categories = _registry.Descriptors.Select(x => x.Category).ToHashSet();
        foreach (var category in new[] { "输入", "图像", "滤波", "增强", "阈值", "形态学", "边缘", "Blob", "轮廓", "几何", "定位", "直线/圆", "变换", "图像运算", "统计", "测量", "标定", "逻辑", "集合", "显示", "AI" })
            Assert.Contains(category, categories);

        Assert.Equal(2, Descriptor("Image.AddWeighted").Inputs.Count);
        Assert.Equal(4, Descriptor("Image.Split").Outputs.Count);
        Assert.Equal(3, Descriptor("Blob.Contours").Outputs.Count);
        Assert.Equal(3, Descriptor("Logic.Switch").Inputs.Count);
        Assert.All(_registry.Descriptors, descriptor => Assert.False(string.IsNullOrWhiteSpace(descriptor.Description)));
        Assert.All(_registry.Descriptors, descriptor =>
        {
            Assert.DoesNotMatch(new Regex("[A-Za-z]"), descriptor.DisplayName);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.EnglishName));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.EnglishDescription));
            Assert.DoesNotMatch(new Regex("[\\u3400-\\u9fff]"), descriptor.EnglishName!);
            Assert.DoesNotMatch(new Regex("[\\u3400-\\u9fff]"), descriptor.EnglishDescription!);
        });
        foreach (var category in _registry.Descriptors.GroupBy(descriptor => descriptor.Category))
        {
            Assert.Equal(category.Count(), category.Select(descriptor => descriptor.DisplayName).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(category.Count(), category.Select(descriptor => descriptor.EnglishName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public async Task Executor_RunsColorThresholdMorphologyContourMeasurementAndCache()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "images", "input"));
        using (var input = new Mat(120, 160, DepthType.Cv8U, 3))
        {
            input.SetTo(new MCvScalar(0, 0, 0));
            CvInvoke.Rectangle(input, new System.Drawing.Rectangle(30, 25, 70, 55), new MCvScalar(0, 0, 255), -1);
            OpenCvFileCodec.Write(Path.Combine(temp.Path, "images", "input", "input.png"), input);
        }

        var load = Node("Image.Load");
        var hsv = Node("Image.BgrToHsv");
        var range = Node("Threshold.InRange");
        range.SetParameter("lower0", 0d); range.SetParameter("lower1", 100d); range.SetParameter("lower2", 100d);
        range.SetParameter("upper0", 10d); range.SetParameter("upper1", 255d); range.SetParameter("upper2", 255d);
        var close = Node("Morphology.Close");
        var contours = Node("Blob.Contours");
        var area = Node("Contour.Area");
        var workflow = new WorkflowDocument
        {
            Nodes = { load, hsv, range, close, contours, area },
            Connections =
            {
                new(load.Id, "Image", hsv.Id, "Image"), new(hsv.Id, "Image", range.Id, "Image"),
                new(range.Id, "Image", close.Id, "Image"), new(close.Id, "Image", contours.Id, "Image"),
                new(load.Id, "Image", area.Id, "Image"), new(contours.Id, "Contours", area.Id, "Contours")
            }
        };
        var cache = new OpenCvProjectCache(temp.Path);
        var executor = new WorkflowExecutor(_registry, temp.Path, cache);
        using (var first = await executor.ExecuteAsync(workflow, area.Id))
        {
            Assert.False(first.GetOutput<Mat>(area.Id, "Image").IsEmpty);
            Assert.InRange(first.GetOutput<double>(area.Id, "Result"), 3500, 4000);
            Assert.All(first.Timings, timing => Assert.False(timing.FromCache));
        }
        using var second = await executor.ExecuteAsync(workflow, area.Id);
        Assert.All(second.Timings, timing => Assert.True(timing.FromCache));
    }

    [Fact]
    public async Task Split_ProducesFourConnectableImageOutputs()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "images", "input"));
        using (var input = new Mat(8, 9, DepthType.Cv8U, 3))
        {
            input.SetTo(new MCvScalar(10, 20, 30));
            OpenCvFileCodec.Write(Path.Combine(temp.Path, "images", "input", "input.png"), input);
        }
        var load = Node("Image.Load"); var split = Node("Image.Split");
        var workflow = new WorkflowDocument { Nodes = { load, split }, Connections = { new(load.Id, "Image", split.Id, "Image") } };
        using var result = await new WorkflowExecutor(_registry, temp.Path).ExecuteAsync(workflow, split.Id);
        for (var i = 0; i < 4; i++) Assert.Equal(1, result.GetOutput<Mat>(split.Id, $"Channel{i}").NumberOfChannels);
    }

    [Fact]
    public async Task Geometry_UsesTypedMultipleInputsAndOutputs()
    {
        var a = Node("Geometry.Point"); a.SetParameter("x", 0d); a.SetParameter("y", 0d);
        var b = Node("Geometry.Point"); b.SetParameter("x", 3d); b.SetParameter("y", 4d);
        var distance = Node("Geometry.Distance");
        var workflow = new WorkflowDocument { Nodes = { a, b, distance }, Connections = { new(a.Id, "Point", distance.Id, "PointA"), new(b.Id, "Point", distance.Id, "PointB") } };
        using var result = await new WorkflowExecutor(_registry).ExecuteAsync(workflow, distance.Id);
        Assert.Equal(5d, result.GetOutput<double>(distance.Id, "Distance"), 8);
    }

    [Fact]
    public void CodeGenerator_RejectsUnsupportedOperatorsInsteadOfEmittingRuntimeWrapper()
    {
        var load = Node("Image.Load"); var hsv = Node("Image.BgrToHsv");
        var workflow = new WorkflowDocument { Nodes = { load, hsv }, Connections = { new(load.Id, "Image", hsv.Id, "Image") } };
        var exception = Assert.Throws<NotSupportedException>(() => new CSharpWorkflowGenerator(_registry).Generate(workflow));
        Assert.Contains("Image.BgrToHsv", exception.Message);
        Assert.Contains("independently executable", exception.Message);
    }

    [Fact]
    public void CodeGenerator_ExtendedExportUsesTheExplicitTargetAndExcludesOtherBranches()
    {
        var firstLoad = Node("Image.Load");
        var firstClose = Node("Morphology.Close");
        var secondLoad = Node("Image.Load");
        var secondClose = Node("Morphology.Close");
        var workflow = new WorkflowDocument
        {
            Nodes = { firstLoad, firstClose, secondLoad, secondClose },
            Connections =
            {
                new(firstLoad.Id, "Image", firstClose.Id, "Image"),
                new(secondLoad.Id, "Image", secondClose.Id, "Image")
            }
        };

        var code = new CSharpWorkflowGenerator(_registry).Generate(workflow, targetNodeId: secondClose.Id);

        Assert.Contains("CvInvoke.MorphologyEx", code);
        Assert.Contains("return mat_", code);
        Assert.Contains(secondClose.Id, code);
        Assert.Contains(secondLoad.Id, code);
        Assert.DoesNotContain(firstLoad.Id, code);
        Assert.DoesNotContain(firstClose.Id, code);
        Assert.DoesNotContain("VisionStudio", code);
        Assert.DoesNotContain("WorkflowJson", code);
    }

    [Fact]
    public void CodeGenerator_ExpandsCrackMeasurementWorkflowIntoOpenCvCalls()
    {
        var load = Node("Image.Load");
        var gray = Node("Image.Gray");
        var blur = Node("Image.GaussianBlur");
        var blackHat = Node("Morphology.BlackHat");
        var otsu = Node("Threshold.Otsu");
        var close = Node("Morphology.Close");
        var contours = Node("Blob.Contours");
        var metrics = Node("Contour.PathMetrics");
        var workflow = new WorkflowDocument
        {
            Nodes = { load, gray, blur, blackHat, otsu, close, contours, metrics },
            Connections =
            {
                new(load.Id, "Image", gray.Id, "Image"),
                new(gray.Id, "Image", blur.Id, "Image"),
                new(blur.Id, "Image", blackHat.Id, "Image"),
                new(blackHat.Id, "Image", otsu.Id, "Image"),
                new(otsu.Id, "Image", close.Id, "Image"),
                new(close.Id, "Image", contours.Id, "Image"),
                new(contours.Id, "Image", metrics.Id, "Image"),
                new(contours.Id, "Contours", metrics.Id, "Contours")
            }
        };

        var code = new CSharpWorkflowGenerator(_registry).Generate(workflow, targetNodeId: metrics.Id);

        Assert.Contains("CvInvoke.GaussianBlur", code);
        Assert.Contains("MorphOp.Blackhat", code);
        Assert.Contains("ThresholdType.Otsu", code);
        Assert.Contains("CvInvoke.FindContours", code);
        Assert.Contains("CvInvoke.MinAreaRect", code);
        Assert.DoesNotContain("VisionStudio.Core", code);
        Assert.DoesNotContain("WorkflowJson.Deserialize", code);
    }

    private OperatorDescriptor Descriptor(string type) { Assert.True(_registry.TryGetDescriptor(type, out var descriptor)); return descriptor; }
    private WorkflowNode Node(string type) => OpenCvOperatorCatalog.CreateNode(Descriptor(type), 0, 0);
}
