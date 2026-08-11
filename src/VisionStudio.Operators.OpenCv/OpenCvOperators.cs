using System.Drawing;
using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using VisionStudio.Core;

namespace VisionStudio.Operators.OpenCv;

public static class OpenCvOperatorCatalog
{
    private static readonly IReadOnlyList<PortDefinition> NoPorts = [];
    private static readonly IReadOnlyList<ParameterDefinition> NoParameters = [];

    public static OperatorRegistry CreateRegistry()
    {
        var registry = new OperatorRegistry();
        RegisterInto(registry);
        return registry;
    }

    public static void RegisterInto(OperatorRegistry registry)
    {
        registry.Register(new("Image.Load", "加载图像", "输入", NoPorts,
            [new("Image", NodeValueType.Mat, DisplayName: "图像", Description: "从项目输入目录读取的原始图像")],
            [new("path", "图像文件", JsonValueKind.String, "images/input/input.png", Description: "点击“选择图片”导入，文件会复制到项目 images/input 目录")],
            "读取项目输入图片，是多数视觉流程的起点。", "Load Image", "Loads an image from the project input directory and outputs an OpenCV matrix."), () => new LoadImageOperator());
        registry.Register(new("Image.Gray", "灰度化", "基础处理",
            [new("Image", NodeValueType.Mat, DisplayName: "输入图像")], [new("Image", NodeValueType.Mat, DisplayName: "灰度图")], NoParameters,
            "将 BGR/BGRA 彩色图转换为单通道灰度图；灰度输入保持不变。", "Convert to Grayscale", "Converts BGR or BGRA input to a single-channel grayscale image. Grayscale input is copied unchanged."), () => new GrayOperator());
        registry.Register(new("Image.GaussianBlur", "高斯模糊", "滤波",
            [new("Image", NodeValueType.Mat, DisplayName: "输入图像")], [new("Image", NodeValueType.Mat, DisplayName: "模糊图像")],
            [new("kernelSize", "核大小", JsonValueKind.Number, 5, 1, 99, "必须是 1–99 之间的奇数；越大，平滑范围越大"), new("sigma", "标准差 Sigma", JsonValueKind.Number, 1.5, 0, 100, "高斯分布标准差，越大图像越平滑")],
            "使用高斯核抑制噪声和细节，常用于二值化或边缘检测之前。", "Gaussian Blur", "Smooths the image with a Gaussian kernel. Use it to suppress noise before thresholding or edge detection."), () => new GaussianBlurOperator());
        registry.Register(new("Image.Threshold", "二值化", "二值处理",
            [new("Image", NodeValueType.Mat, DisplayName: "灰度图")], [new("Image", NodeValueType.Mat, DisplayName: "二值图")],
            [new("threshold", "阈值", JsonValueKind.Number, 128d, 0, 255, "像素值与此值比较决定黑白输出"), new("maxValue", "最大值", JsonValueKind.Number, 255d, 0, 255, "满足阈值条件时写入的像素值"), new("mode", "阈值模式", JsonValueKind.String, "Binary", Description: "OpenCV 阈值模式，例如 Binary、BinaryInv 或 Otsu")],
            "按照阈值把灰度图转换为黑白图，用于前景分割和缺陷区域提取。", "Global Threshold", "Applies a fixed global threshold to a grayscale image and outputs a binary image."), () => new ThresholdOperator());
        registry.Register(new("Image.AddWeighted", "加权融合", "图像运算",
            [new("ImageA", NodeValueType.Mat, DisplayName: "图像 A"), new("ImageB", NodeValueType.Mat, DisplayName: "图像 B")], [new("Image", NodeValueType.Mat, DisplayName: "融合图像")],
            [new("alpha", "图像 A 权重", JsonValueKind.Number, 0.5d, Description: "第一幅图像的混合权重"), new("beta", "图像 B 权重", JsonValueKind.Number, 0.5d, Description: "第二幅图像的混合权重"), new("gamma", "亮度偏移", JsonValueKind.Number, 0d, Description: "融合后加到每个像素的常量")],
            "按权重融合两幅尺寸、位深和通道数一致的图像。", "Weighted Addition", "Blends two images of equal size, depth, and channel count using configurable weights and an offset."), () => new AddWeightedOperator());
        registry.Register(new("Image.Save", "保存图像", "输出",
            [new("Image", NodeValueType.Mat, DisplayName: "待保存图像")], [new("Image", NodeValueType.Mat, DisplayName: "输出图像")],
            [new("path", "输出文件", JsonValueKind.String, "images/output/result.png", Description: "相对于项目根目录的输出路径")],
            "把输入图像写入项目 images/output；该节点具有文件写入副作用，不使用结果缓存。", "Save Image", "Writes the input image to the project output directory and passes a cloned image downstream."), () => new SaveImageOperator());
        ExtendedOperatorCatalog.RegisterInto(registry);
    }

    public static WorkflowNode CreateNode(OperatorDescriptor descriptor, double x, double y)
    {
        var node = new WorkflowNode { Id = Guid.NewGuid().ToString("N"), Type = descriptor.Type, Name = descriptor.DisplayName, X = x, Y = y };
        foreach (var parameter in descriptor.Parameters) node.SetParameter(parameter.Name, parameter.DefaultValue);
        return node;
    }
}

public static class OpenCvFileCodec
{
    public static Mat Read(string path, ImreadModes mode = ImreadModes.Unchanged)
    {
        var output = new Mat();
        try
        {
            using var stream = File.OpenRead(path);
            CvInvoke.Imdecode(stream, mode, output);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    public static void Write(string path, Mat image)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension)) throw new InvalidOperationException("Image output path requires a file extension.");
        var bytes = CvInvoke.Imencode(extension, image, []);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}

public sealed class OpenCvProjectCache(string projectRoot) : INodeOutputCache
{
    private readonly string _projectRoot = Path.GetFullPath(projectRoot);

    public string GetExternalFingerprint(WorkflowNode node, string workingDirectory)
    {
        if (node.Type is not ("Image.Load" or "Input.Video")) return "";
        var fallback = node.Type == "Input.Video" ? "images/input/video.mp4" : "images/input/input.png";
        var path = ProjectPaths.ResolveInside(workingDirectory, node.GetParameter("path", fallback));
        if (!File.Exists(path)) return "missing";
        var info = new FileInfo(path);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    public bool TryRestore(WorkflowNode node, OperatorDescriptor descriptor, string cacheKey, out IReadOnlyDictionary<string, object> outputs)
    {
        outputs = new Dictionary<string, object>();
        if (node.Type is "Image.Save" or "Input.Camera" || descriptor.Outputs.Any(x => !CanPersist(x.Type))) return false;
        var restored = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            foreach (var port in descriptor.Outputs)
            {
                var path = CachePath(node.Id, cacheKey, port.Name, port.Type);
                if (!File.Exists(path)) return false;
                if (port.Type == NodeValueType.Mat)
                {
                    var mat = OpenCvFileCodec.Read(path);
                    if (mat.IsEmpty) { mat.Dispose(); return false; }
                    restored.Add(port.Name, mat);
                }
                else restored.Add(port.Name, ReadValue(path, port.Type));
            }
            outputs = restored;
            return true;
        }
        finally
        {
            if (outputs.Count == 0)
                foreach (var disposable in restored.Values.OfType<IDisposable>()) disposable.Dispose();
        }
    }

    public ValueTask StoreAsync(WorkflowNode node, OperatorDescriptor descriptor, string cacheKey, IReadOnlyDictionary<string, object> outputs, CancellationToken cancellationToken)
    {
        if (node.Type is "Image.Save" or "Input.Camera" || descriptor.Outputs.Any(x => !CanPersist(x.Type))) return ValueTask.CompletedTask;
        foreach (var port in descriptor.Outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!outputs.TryGetValue(port.Name, out var value)) continue;
            var path = CachePath(node.Id, cacheKey, port.Name, port.Type);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}{Path.GetExtension(path)}");
            try
            {
                if (port.Type == NodeValueType.Mat && value is Mat mat) OpenCvFileCodec.Write(temporary, mat);
                else File.WriteAllText(temporary, JsonSerializer.Serialize(value, value.GetType(), WorkflowJson.Options));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return ValueTask.CompletedTask;
    }

    private static bool CanPersist(NodeValueType type) => type is not (NodeValueType.Any or NodeValueType.Collection or NodeValueType.Matrix);

    private static object ReadValue(string path, NodeValueType type)
    {
        var json = File.ReadAllText(path);
        return type switch
        {
            NodeValueType.Integer => JsonSerializer.Deserialize<int>(json, WorkflowJson.Options),
            NodeValueType.Double => JsonSerializer.Deserialize<double>(json, WorkflowJson.Options),
            NodeValueType.Boolean => JsonSerializer.Deserialize<bool>(json, WorkflowJson.Options),
            NodeValueType.String => JsonSerializer.Deserialize<string>(json, WorkflowJson.Options) ?? "",
            NodeValueType.Point => JsonSerializer.Deserialize<VisionPoint>(json, WorkflowJson.Options)!,
            NodeValueType.Points => JsonSerializer.Deserialize<List<VisionPoint>>(json, WorkflowJson.Options)!,
            NodeValueType.Line => JsonSerializer.Deserialize<VisionLine>(json, WorkflowJson.Options)!,
            NodeValueType.Circle => JsonSerializer.Deserialize<VisionCircle>(json, WorkflowJson.Options)!,
            NodeValueType.Rectangle => JsonSerializer.Deserialize<Rectangle>(json, WorkflowJson.Options),
            NodeValueType.Contours => JsonSerializer.Deserialize<VisionContours>(json, WorkflowJson.Options)!,
            _ => throw new NotSupportedException($"Cannot restore cache value type {type}.")
        };
    }

    private string CachePath(string nodeId, string cacheKey, string port, NodeValueType type)
    {
        static string Safe(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var extension = type == NodeValueType.Mat ? ".png" : ".json";
        return ProjectPaths.ResolveInside(_projectRoot, $"images/cache/{Safe(nodeId)}/{cacheKey}-{Safe(port)}{extension}");
    }
}

internal sealed class LoadImageOperator : IVisionOperator
{
    public ValueTask ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relativePath = context.GetParameter("path", "images/input/input.png");
        var path = context.ResolveProjectPath(relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Input image does not exist.", path);
        var image = OpenCvFileCodec.Read(path);
        if (image.IsEmpty) { image.Dispose(); throw new InvalidDataException($"OpenCV could not decode '{relativePath}'."); }
        context.SetOutput("Image", image);
        return ValueTask.CompletedTask;
    }
}

internal sealed class GrayOperator : IVisionOperator
{
    public ValueTask ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.GetInput<Mat>("Image");
        var output = new Mat();
        if (source.NumberOfChannels == 1) source.CopyTo(output);
        else CvInvoke.CvtColor(source, output, source.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);
        context.SetOutput("Image", output);
        return ValueTask.CompletedTask;
    }
}

internal sealed class GaussianBlurOperator : IVisionOperator
{
    public ValueTask ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kernel = context.GetParameter("kernelSize", 5);
        if (kernel < 1 || kernel > 99 || kernel % 2 == 0) throw new ArgumentOutOfRangeException("kernelSize", "Kernel size must be odd and between 1 and 99.");
        var sigma = context.GetParameter("sigma", 1.5d);
        if (sigma < 0) throw new ArgumentOutOfRangeException("sigma");
        var output = new Mat();
        CvInvoke.GaussianBlur(context.GetInput<Mat>("Image"), output, new Size(kernel, kernel), sigma);
        context.SetOutput("Image", output);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ThresholdOperator : IVisionOperator
{
    public ValueTask ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var threshold = context.GetParameter("threshold", 128d);
        var maxValue = context.GetParameter("maxValue", 255d);
        if (threshold is < 0 or > 255) throw new ArgumentOutOfRangeException("threshold");
        if (maxValue is < 0 or > 255) throw new ArgumentOutOfRangeException("maxValue");
        var modeName = context.GetParameter("mode", "Binary");
        if (!Enum.TryParse<ThresholdType>(modeName, ignoreCase: true, out var mode)) throw new ArgumentException($"Unknown threshold mode '{modeName}'.");
        var output = new Mat();
        CvInvoke.Threshold(context.GetInput<Mat>("Image"), output, threshold, maxValue, mode);
        context.SetOutput("Image", output);
        return ValueTask.CompletedTask;
    }
}

internal sealed class AddWeightedOperator : IVisionOperator
{
    public ValueTask ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var a = context.GetInput<Mat>("ImageA");
        var b = context.GetInput<Mat>("ImageB");
        if (a.Size != b.Size || a.Depth != b.Depth || a.NumberOfChannels != b.NumberOfChannels)
            throw new ArgumentException("Add Weighted inputs must have equal size, depth and channel count.");
        var output = new Mat();
        CvInvoke.AddWeighted(a, context.GetParameter("alpha", .5d), b, context.GetParameter("beta", .5d), context.GetParameter("gamma", 0d), output);
        context.SetOutput("Image", output);
        return ValueTask.CompletedTask;
    }
}

internal sealed class SaveImageOperator : IVisionOperator
{
    public ValueTask ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.GetInput<Mat>("Image");
        var path = context.ResolveProjectPath(context.GetParameter("path", "images/output/result.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        OpenCvFileCodec.Write(path, source);
        var output = new Mat();
        source.CopyTo(output);
        context.SetOutput("Image", output);
        return ValueTask.CompletedTask;
    }
}
