using Emgu.CV;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Tests;

public sealed class VisionAiOperatorTests
{
    private readonly OperatorRegistry registry = OpenCvOperatorCatalog.CreateRegistry();

    [Fact]
    [Trait("Dimension", "VisionAI")]
    public void Catalog_ExposesOnlyImplementedVisionAiOperators()
    {
        Assert.False(registry.TryGetDescriptor("AI.Classification", out _));
        AssertDescriptor("AI.Detection", ["Image", "Result", "Count"], ["provider", "deviceId", "model", "confidence", "nms"]);
        AssertDescriptor("AI.Segmentation", ["Image", "Result", "Count"], ["provider", "deviceId", "model", "confidence", "nms", "maskThreshold"]);
        AssertDescriptor("AI.OCR", ["Image", "Result", "Count"], ["provider", "deviceId", "detectionModel", "recognitionModel", "dictionary"]);
        AssertDescriptor("AI.Anomaly", ["Image", "Result", "Score", "Anomalous"], ["provider", "deviceId", "model", "inputWidth", "inputHeight", "imageThreshold", "pixelThreshold", "preprocess"]);
    }

    [Fact]
    [Trait("Dimension", "VisionAI")]
    public void CacheFingerprint_ChangesWhenProjectModelChanges()
    {
        using var project = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "models"));
        var model = Path.Combine(project.Path, "models", "detection.onnx");
        File.WriteAllBytes(model, [1, 2, 3]);
        var node = Node("AI.Detection");
        node.SetParameter("model", "models/detection.onnx");
        var cache = new OpenCvProjectCache(project.Path);
        var before = cache.GetExternalFingerprint(node, project.Path);
        File.WriteAllBytes(model, [1, 2, 3, 4]);
        var after = cache.GetExternalFingerprint(node, project.Path);
        Assert.NotEqual(before, after);
    }

    [Fact]
    [Trait("Dimension", "VisionAI")]
    public async Task Detection_ExecutesPackagedNativeSdkAndProducesRealResults()
    {
        var runtime = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_RUNTIME");
        var model = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_DETECTION_MODEL");
        var image = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(runtime) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(image)) return;
        Assert.True(File.Exists(Path.Combine(runtime, "vision_ai.dll")) || File.Exists(Path.Combine(runtime, "bin", "vision_ai.dll")), $"VisionAI runtime not found: {runtime}");
        Assert.True(File.Exists(model), $"Detection model not found: {model}");
        Assert.True(File.Exists(image), $"Test image not found: {image}");

        using var project = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "images", "input"));
        Directory.CreateDirectory(Path.Combine(project.Path, "models"));
        File.Copy(image, Path.Combine(project.Path, "images", "input", "input.png"));
        File.Copy(model, Path.Combine(project.Path, "models", "detection.onnx"));

        var load = Node("Image.Load");
        var detection = Node("AI.Detection");
        detection.SetParameter("model", "models/detection.onnx");
        detection.SetParameter("provider", "CPU");
        detection.SetParameter("confidence", .25d);
        var workflow = new WorkflowDocument
        {
            Nodes = { load, detection },
            Connections = { new(load.Id, "Image", detection.Id, "Image") }
        };

        using var result = await new WorkflowExecutor(registry, project.Path).ExecuteAsync(workflow, detection.Id);
        var output = result.GetOutput<Mat>(detection.Id, "Image");
        var count = result.GetOutput<int>(detection.Id, "Count");
        var json = result.GetOutput<string>(detection.Id, "Result");
        Assert.False(output.IsEmpty);
        using var source = OpenCvFileCodec.Read(Path.Combine(project.Path, "images", "input", "input.png"));
        Assert.Equal(output.Size, source.Size);
        Assert.True(count > 0, "The positive object image should produce at least one detection.");
        Assert.Contains("Confidence", json);
    }

    [Fact]
    [Trait("Dimension", "VisionAI")]
    public async Task Segmentation_ExecutesPackagedNativeSdkAndReturnsMasks()
    {
        var model = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_SEGMENTATION_MODEL");
        var image = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_RUNTIME")) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(image)) return;
        using var project = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "images", "input"));
        Directory.CreateDirectory(Path.Combine(project.Path, "models"));
        File.Copy(image, Path.Combine(project.Path, "images", "input", "input.png"));
        File.Copy(model, Path.Combine(project.Path, "models", "segmentation.onnx"));
        var load = Node("Image.Load");
        var segmentation = Node("AI.Segmentation");
        segmentation.SetParameter("model", "models/segmentation.onnx");
        segmentation.SetParameter("provider", "CPU");
        var workflow = new WorkflowDocument { Nodes = { load, segmentation }, Connections = { new(load.Id, "Image", segmentation.Id, "Image") } };
        using var result = await new WorkflowExecutor(registry, project.Path).ExecuteAsync(workflow, segmentation.Id);
        Assert.False(result.GetOutput<Mat>(segmentation.Id, "Image").IsEmpty);
        Assert.True(result.GetOutput<int>(segmentation.Id, "Count") > 0, "The positive object image should produce at least one segment.");
        Assert.Contains("MaskWidth", result.GetOutput<string>(segmentation.Id, "Result"));
    }

    [Fact]
    [Trait("Dimension", "VisionAI")]
    public async Task Ocr_ExecutesPackagedNativeSdkAndReturnsUtf8Result()
    {
        var detectionModel = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_OCR_DETECTION_MODEL");
        var recognitionModel = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_OCR_RECOGNITION_MODEL");
        var dictionary = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_OCR_DICTIONARY");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_RUNTIME")) || string.IsNullOrWhiteSpace(detectionModel) || string.IsNullOrWhiteSpace(recognitionModel) || string.IsNullOrWhiteSpace(dictionary)) return;
        using var project = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "images", "input"));
        Directory.CreateDirectory(Path.Combine(project.Path, "models", "ocr"));
        using (var synthetic = new Mat(240, 800, Emgu.CV.CvEnum.DepthType.Cv8U, 3))
        {
            synthetic.SetTo(new Emgu.CV.Structure.MCvScalar(255, 255, 255));
            CvInvoke.PutText(synthetic, "VISION AI 123", new System.Drawing.Point(35, 145), Emgu.CV.CvEnum.FontFace.HersheySimplex, 2.2, new Emgu.CV.Structure.MCvScalar(0, 0, 0), 5);
            OpenCvFileCodec.Write(Path.Combine(project.Path, "images", "input", "input.png"), synthetic);
        }
        File.Copy(detectionModel, Path.Combine(project.Path, "models", "ocr", "detection.onnx"));
        File.Copy(recognitionModel, Path.Combine(project.Path, "models", "ocr", "recognition.onnx"));
        File.Copy(dictionary, Path.Combine(project.Path, "models", "ocr", "dictionary.txt"));
        var load = Node("Image.Load");
        var ocr = Node("AI.OCR");
        ocr.SetParameter("detectionModel", "models/ocr/detection.onnx");
        ocr.SetParameter("recognitionModel", "models/ocr/recognition.onnx");
        ocr.SetParameter("dictionary", "models/ocr/dictionary.txt");
        ocr.SetParameter("provider", "CPU");
        var workflow = new WorkflowDocument { Nodes = { load, ocr }, Connections = { new(load.Id, "Image", ocr.Id, "Image") } };
        using var result = await new WorkflowExecutor(registry, project.Path).ExecuteAsync(workflow, ocr.Id);
        Assert.False(result.GetOutput<Mat>(ocr.Id, "Image").IsEmpty);
        Assert.True(result.GetOutput<int>(ocr.Id, "Count") > 0, "The synthetic text image should produce at least one OCR result.");
        Assert.StartsWith("[", result.GetOutput<string>(ocr.Id, "Result"));
    }

    [Fact]
    [Trait("Dimension", "VisionAI")]
    public async Task Anomaly_ExecutesPackagedNativeSdkAndReturnsHeatmap()
    {
        var model = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_ANOMALY_MODEL");
        var image = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_RUNTIME")) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(image)) return;
        using var project = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "images", "input"));
        Directory.CreateDirectory(Path.Combine(project.Path, "models"));
        File.Copy(image, Path.Combine(project.Path, "images", "input", "input.png"));
        File.Copy(model, Path.Combine(project.Path, "models", "anomaly.onnx"));
        var load = Node("Image.Load");
        var anomaly = Node("AI.Anomaly");
        anomaly.SetParameter("model", "models/anomaly.onnx");
        anomaly.SetParameter("provider", "CPU");
        anomaly.SetParameter("inputWidth", 64);
        anomaly.SetParameter("inputHeight", 64);
        anomaly.SetParameter("preprocess", "RGB01");
        var workflow = new WorkflowDocument { Nodes = { load, anomaly }, Connections = { new(load.Id, "Image", anomaly.Id, "Image") } };
        using var result = await new WorkflowExecutor(registry, project.Path).ExecuteAsync(workflow, anomaly.Id);
        Assert.False(result.GetOutput<Mat>(anomaly.Id, "Image").IsEmpty);
        Assert.True(double.IsFinite(result.GetOutput<double>(anomaly.Id, "Score")));
        Assert.Contains("MapWidth", result.GetOutput<string>(anomaly.Id, "Result"));
        _ = result.GetOutput<bool>(anomaly.Id, "Anomalous");
    }

    private void AssertDescriptor(string type, string[] outputs, string[] parameters)
    {
        Assert.True(registry.TryGetDescriptor(type, out var descriptor));
        Assert.Equal(outputs, descriptor.Outputs.Select(port => port.Name));
        foreach (var parameter in parameters) Assert.Contains(descriptor.Parameters, item => item.Name == parameter);
        Assert.DoesNotContain("透传", descriptor.Description ?? string.Empty);
    }

    private WorkflowNode Node(string type)
    {
        Assert.True(registry.TryGetDescriptor(type, out var descriptor));
        return OpenCvOperatorCatalog.CreateNode(descriptor, 0, 0);
    }
}
