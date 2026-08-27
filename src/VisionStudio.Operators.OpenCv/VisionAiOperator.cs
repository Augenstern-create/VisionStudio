using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using VisionStudio.Core;

namespace VisionStudio.Operators.OpenCv;

internal static class VisionAiOperator {
  public static void Execute(string type, NodeExecutionContext context) {
    var api = VisionAiNative.Load();
    var contextOptions = new VisionAiNative.ContextOptions();
    api.ContextOptionsInit(ref contextOptions);
    VisionAiNative.EnsureSuccess(api, api.ContextCreate(ref contextOptions, out nint nativeContext));
    if (nativeContext == 0) {
      throw new InvalidOperationException("VisionAI 未返回有效的上下文。");
    }

    try {
      using var image = PrepareInput(context.GetInput<Mat>("Image"));
      var nativeImage = ToNativeImage(image);
      switch (type) {
        case "AI.Detection": Detect(api, nativeContext, context, image, ref nativeImage); break;
        case "AI.Segmentation": Segment(api, nativeContext, context, image, ref nativeImage); break;
        case "AI.OCR": Recognize(api, nativeContext, context, image, ref nativeImage); break;
        case "AI.Anomaly": DetectAnomaly(api, nativeContext, context, image, ref nativeImage); break;
        default: throw new NotSupportedException($"不支持 VisionAI 算子 '{type}'。");
      }
    } finally {
      api.ContextDestroy(nativeContext);
    }
  }

  private static void Detect(VisionAiNative api, nint nativeContext, NodeExecutionContext context, Mat image, ref VisionAiNative.Image nativeImage) {
    string model = RequiredProjectFile(context, "model", "models/detection.onnx");
    nint modelPointer = Marshal.StringToCoTaskMemUTF8(model);
    nint detector = 0;
    try {
      var options = new VisionAiNative.DetectorOptions();
      api.DetectorOptionsInit(ref options);
      options.ModelPath = modelPointer;
      options.ConfidenceThreshold = (float)context.GetParameter("confidence", .25d);
      options.NmsIouThreshold = (float)context.GetParameter("nms", .45d);
      options.Provider = Provider(context.GetParameter("provider", "CPU"));
      options.DeviceId = context.GetParameter("deviceId", 0);
      VisionAiNative.EnsureSuccess(api, api.DetectorCreate(nativeContext, ref options, out detector));

      VisionAiNative.EnsureQueryStatus(api, api.DetectorDetect(detector, ref nativeImage, 0, 0, out nuint count));
      nint detectionsPointer = Allocate<VisionAiNative.Detection>(count);
      VisionAiNative.Detection[] detections;
      try {
        VisionAiNative.EnsureSuccess(api, api.DetectorDetect(detector, ref nativeImage, detectionsPointer, count, out nuint filled));
        EnsureCount("目标检测", count, filled);
        detections = MarshalArray<VisionAiNative.Detection>(detectionsPointer, (int)filled);
      } finally {
        Free(detectionsPointer);
      }
      string[] labels = ReadLabels(context);
      var output = ToColor(image);
      foreach (var detection in detections) {
        var rectangle = ClampRectangle(detection.X, detection.Y, detection.Width, detection.Height, output.Size);
        if (rectangle.Width > 0 && rectangle.Height > 0) {
          CvInvoke.Rectangle(output, rectangle, new MCvScalar(0, 220, 0), 2);
        }

        PutLabel(output, rectangle.Location, $"{Label(labels, detection.ClassId)} {detection.Confidence:P0}");
      }
      context.SetOutput("Image", output);
      context.SetOutput("Result", JsonSerializer.Serialize(detections.Select(d => new { d.ClassId, Label = Label(labels, d.ClassId), d.Confidence, d.X, d.Y, d.Width, d.Height })));
      context.SetOutput("Count", detections.Length);
    } finally {
      if (detector != 0) {
        api.DetectorDestroy(detector);
      }

      Marshal.FreeCoTaskMem(modelPointer);
    }
  }

  private static void Segment(VisionAiNative api, nint nativeContext, NodeExecutionContext context, Mat image, ref VisionAiNative.Image nativeImage) {
    string model = RequiredProjectFile(context, "model", "models/segmentation.onnx");
    nint modelPointer = Marshal.StringToCoTaskMemUTF8(model);
    nint segmenter = 0;
    try {
      var options = new VisionAiNative.SegmenterOptions();
      api.SegmenterOptionsInit(ref options);
      options.ModelPath = modelPointer;
      options.ConfidenceThreshold = (float)context.GetParameter("confidence", .25d);
      options.NmsIouThreshold = (float)context.GetParameter("nms", .45d);
      options.MaskThreshold = (float)context.GetParameter("maskThreshold", .5d);
      options.Provider = Provider(context.GetParameter("provider", "CPU"));
      options.DeviceId = context.GetParameter("deviceId", 0);
      VisionAiNative.EnsureSuccess(api, api.SegmenterCreate(nativeContext, ref options, out segmenter));
      VisionAiNative.EnsureQueryStatus(api, api.SegmenterSegment(segmenter, ref nativeImage, 0, 0, 0, 0, out nuint count, out nuint maskBytes));

      nint segmentsPointer = Allocate<VisionAiNative.Segment>(count);
      nint masksPointer = AllocateBytes(maskBytes);
      try {
        VisionAiNative.EnsureSuccess(api, api.SegmenterSegment(segmenter, ref nativeImage, segmentsPointer, count, masksPointer, maskBytes, out nuint filled, out nuint filledMaskBytes));
        EnsureCount("实例分割", count, filled);
        if (filledMaskBytes > maskBytes) {
          throw new InvalidDataException("VisionAI 返回的掩膜数据超过已查询容量。");
        }

        var segments = MarshalArray<VisionAiNative.Segment>(segmentsPointer, (int)filled);
        byte[] masks = new byte[checked((int)filledMaskBytes)];
        if (masks.Length > 0) {
          Marshal.Copy(masksPointer, masks, 0, masks.Length);
        }

        string[] labels = ReadLabels(context);
        var output = ToColor(image);
        for (int i = 0; i < segments.Length; i++) {
          DrawSegment(output, segments[i], masks, i);
        }

        foreach (var segment in segments) {
          var rectangle = ClampRectangle(segment.X, segment.Y, segment.Width, segment.Height, output.Size);
          if (rectangle.Width > 0 && rectangle.Height > 0) {
            CvInvoke.Rectangle(output, rectangle, new MCvScalar(255, 255, 255), 2);
          }

          PutLabel(output, rectangle.Location, $"{Label(labels, segment.ClassId)} {segment.Confidence:P0}");
        }
        context.SetOutput("Image", output);
        context.SetOutput("Result", JsonSerializer.Serialize(segments.Select(s => new { s.ClassId, Label = Label(labels, s.ClassId), s.Confidence, s.X, s.Y, s.Width, s.Height, s.MaskX, s.MaskY, s.MaskWidth, s.MaskHeight })));
        context.SetOutput("Count", segments.Length);
      } finally {
        Free(segmentsPointer);
        Free(masksPointer);
      }
    } finally {
      if (segmenter != 0) {
        api.SegmenterDestroy(segmenter);
      }

      Marshal.FreeCoTaskMem(modelPointer);
    }
  }

  private static void Recognize(VisionAiNative api, nint nativeContext, NodeExecutionContext context, Mat image, ref VisionAiNative.Image nativeImage) {
    string[] paths =
    [
            RequiredProjectFile(context, "detectionModel", "models/ocr/detection.onnx"),
            RequiredProjectFile(context, "recognitionModel", "models/ocr/recognition.onnx"),
            RequiredProjectFile(context, "dictionary", "models/ocr/dictionary.txt")
        ];
    nint[] pointers = [.. paths.Select(Marshal.StringToCoTaskMemUTF8)];
    nint ocr = 0;
    try {
      var options = new VisionAiNative.OcrOptions();
      api.OcrOptionsInit(ref options);
      options.DetectionModelPath = pointers[0];
      options.RecognitionModelPath = pointers[1];
      options.CharacterDictPath = pointers[2];
      options.DetectionThreshold = (float)context.GetParameter("detectionThreshold", .3d);
      options.BoxThreshold = (float)context.GetParameter("boxThreshold", .6d);
      options.UnclipRatio = (float)context.GetParameter("unclipRatio", 1.5d);
      options.Provider = Provider(context.GetParameter("provider", "CPU"));
      options.DeviceId = context.GetParameter("deviceId", 0);
      VisionAiNative.EnsureSuccess(api, api.OcrCreate(nativeContext, ref options, out ocr));
      VisionAiNative.EnsureQueryStatus(api, api.OcrRecognize(ocr, ref nativeImage, 0, 0, 0, 0, out nuint count, out nuint textBytes));
      nint resultsPointer = Allocate<VisionAiNative.OcrResult>(count);
      nint textPointer = AllocateBytes(textBytes);
      try {
        VisionAiNative.EnsureSuccess(api, api.OcrRecognize(ocr, ref nativeImage, resultsPointer, count, textPointer, textBytes, out nuint filled, out nuint filledTextBytes));
        EnsureCount("文字识别", count, filled);
        var results = MarshalArray<VisionAiNative.OcrResult>(resultsPointer, (int)filled);
        byte[] text = new byte[checked((int)filledTextBytes)];
        if (text.Length > 0) {
          Marshal.Copy(textPointer, text, 0, text.Length);
        }

        var output = ToColor(image);
        var values = results.Select(result => new {
          Text = DecodeText(text, result.TextOffset, result.TextLength),
          result.Confidence,
          Points = new[] { result.X0, result.Y0, result.X1, result.Y1, result.X2, result.Y2, result.X3, result.Y3 }
        }).ToArray();
        foreach (var value in values) {
          var points = Enumerable.Range(0, 4).Select(i => new Point((int)Math.Round(value.Points[i * 2]), (int)Math.Round(value.Points[i * 2 + 1]))).ToArray();
          CvInvoke.Polylines(output, points, true, new MCvScalar(0, 220, 0), 2);
          PutLabel(output, points[0], $"{value.Text} {value.Confidence:P0}");
        }
        context.SetOutput("Image", output);
        context.SetOutput("Result", JsonSerializer.Serialize(values));
        context.SetOutput("Count", values.Length);
      } finally {
        Free(resultsPointer);
        Free(textPointer);
      }
    } finally {
      if (ocr != 0) {
        api.OcrDestroy(ocr);
      }

      foreach (nint pointer in pointers) {
        Marshal.FreeCoTaskMem(pointer);
      }
    }
  }

  private static void DetectAnomaly(VisionAiNative api, nint nativeContext, NodeExecutionContext context, Mat image, ref VisionAiNative.Image nativeImage) {
    string model = RequiredProjectFile(context, "model", "models/anomaly.onnx");
    nint modelPointer = Marshal.StringToCoTaskMemUTF8(model);
    nint detector = 0;
    try {
      var options = new VisionAiNative.AnomalyOptions();
      api.AnomalyOptionsInit(ref options);
      options.ModelPath = modelPointer;
      options.InputWidth = context.GetParameter("inputWidth", 256);
      options.InputHeight = context.GetParameter("inputHeight", 256);
      options.ImageThreshold = (float)context.GetParameter("imageThreshold", .5d);
      options.PixelThreshold = (float)context.GetParameter("pixelThreshold", .5d);
      options.Preprocess = context.GetParameter("preprocess", "ImageNet").Equals("RGB01", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
      options.Provider = Provider(context.GetParameter("provider", "CPU"));
      options.DeviceId = context.GetParameter("deviceId", 0);
      VisionAiNative.EnsureSuccess(api, api.AnomalyCreate(nativeContext, ref options, out detector));
      var result = new VisionAiNative.AnomalyResult();
      VisionAiNative.EnsureQueryStatus(api, api.AnomalyDetect(detector, ref nativeImage, ref result, 0, 0, 0, 0, out nuint elements));
      nint heatmapPointer = AllocateBytes(checked(elements * (nuint)sizeof(float)));
      nint maskPointer = AllocateBytes(elements);
      try {
        VisionAiNative.EnsureSuccess(api, api.AnomalyDetect(detector, ref nativeImage, ref result, heatmapPointer, elements, maskPointer, elements, out nuint filled));
        if (filled > elements) {
          throw new InvalidDataException("VisionAI 返回的异常图数据超过已查询容量。");
        }

        float[] heatmap = new float[checked((int)filled)];
        if (heatmap.Length > 0) {
          Marshal.Copy(heatmapPointer, heatmap, 0, heatmap.Length);
        }

        using var heatmapMat = new Mat(result.MapHeight, result.MapWidth, DepthType.Cv32F, 1);
        if (heatmap.Length > 0) {
          Marshal.Copy(heatmap, 0, heatmapMat.DataPointer, heatmap.Length);
        }

        using var normalized = new Mat();
        CvInvoke.Normalize(heatmapMat, normalized, 0, 255, NormType.MinMax, DepthType.Cv8U);
        using var resized = new Mat();
        CvInvoke.Resize(normalized, resized, image.Size);
        using var colored = new Mat();
        CvInvoke.ApplyColorMap(resized, colored, ColorMapType.Jet);
        using var source = ToColor(image);
        var output = new Mat();
        CvInvoke.AddWeighted(source, .55, colored, .45, 0, output);
        PutLabel(output, new Point(8, 24), $"Anomaly: {result.Score:F4} ({(result.Anomalous != 0 ? "YES" : "NO")})");
        context.SetOutput("Image", output);
        context.SetOutput("Result", JsonSerializer.Serialize(new { result.Score, Anomalous = result.Anomalous != 0, result.MapWidth, result.MapHeight }));
        context.SetOutput("Score", (double)result.Score);
        context.SetOutput("Anomalous", result.Anomalous != 0);
      } finally {
        Free(heatmapPointer);
        Free(maskPointer);
      }
    } finally {
      if (detector != 0) {
        api.AnomalyDestroy(detector);
      }

      Marshal.FreeCoTaskMem(modelPointer);
    }
  }

  private static Mat PrepareInput(Mat source) {
    if (source.IsEmpty) {
      throw new InvalidDataException("VisionAI 输入图像为空。");
    }

    if (source.NumberOfChannels is not (1 or 3 or 4)) {
      throw new NotSupportedException($"VisionAI 只支持 1、3 或 4 通道图像，当前为 {source.NumberOfChannels} 通道。");
    }

    if (source.Depth == DepthType.Cv8U) {
      return source.Clone();
    }

    var converted = new Mat();
    source.ConvertTo(converted, DepthType.Cv8U);
    return converted;
  }

  private static VisionAiNative.Image ToNativeImage(Mat image) {
    return new() {
      StructSize = (uint)Marshal.SizeOf<VisionAiNative.Image>(),
      Data = image.DataPointer,
      Width = image.Cols,
      Height = image.Rows,
      StrideBytes = (nuint)image.Step,
      PixelFormat = image.NumberOfChannels switch { 1 => 4, 3 => 1, 4 => 3, _ => throw new NotSupportedException() }
    };
  }

  private static Mat ToColor(Mat image) {
    if (image.NumberOfChannels == 3) {
      return image.Clone();
    }

    var output = new Mat();
    CvInvoke.CvtColor(image, output, image.NumberOfChannels == 1 ? ColorConversion.Gray2Bgr : ColorConversion.Bgra2Bgr);
    return output;
  }

  private static void DrawSegment(Mat output, VisionAiNative.Segment segment, byte[] masks, int index) {
    if (segment.MaskWidth <= 0 || segment.MaskHeight <= 0) {
      return;
    }

    var rectangle = Rectangle.Intersect(new Rectangle(segment.MaskX, segment.MaskY, segment.MaskWidth, segment.MaskHeight), new Rectangle(Point.Empty, output.Size));
    if (rectangle.Width <= 0 || rectangle.Height <= 0) {
      return;
    }

    int offset = checked((int)segment.MaskOffset);
    int stride = checked((int)segment.MaskStrideBytes);
    if (offset < 0 || stride < segment.MaskWidth || offset + (segment.MaskHeight - 1) * stride + segment.MaskWidth > masks.Length) {
      throw new InvalidDataException($"VisionAI 第 {index + 1} 个实例的掩膜范围无效。");
    }

    byte[] clipped = new byte[rectangle.Width * rectangle.Height];
    int sourceX = rectangle.X - segment.MaskX;
    int sourceY = rectangle.Y - segment.MaskY;
    for (int row = 0; row < rectangle.Height; row++) {
      Array.Copy(masks, offset + (sourceY + row) * stride + sourceX, clipped, row * rectangle.Width, rectangle.Width);
    }

    using var mask = new Mat(rectangle.Height, rectangle.Width, DepthType.Cv8U, 1);
    Marshal.Copy(clipped, 0, mask.DataPointer, clipped.Length);
    using var roi = new Mat(output, rectangle);
    var colors = new[] { new MCvScalar(40, 180, 255), new MCvScalar(255, 100, 40), new MCvScalar(80, 220, 80), new MCvScalar(220, 80, 220) };
    roi.SetTo(colors[index % colors.Length], mask);
  }

  private static string RequiredProjectFile(NodeExecutionContext context, string parameter, string fallback) {
    string relative = context.GetParameter(parameter, fallback);
    if (string.IsNullOrWhiteSpace(relative)) {
      throw new InvalidDataException($"参数 '{parameter}' 未配置项目内文件。");
    }

    string path = context.ResolveProjectPath(relative);
    if (!File.Exists(path)) {
      throw new FileNotFoundException($"VisionAI 参数 '{parameter}' 指向的文件不存在。", path);
    }

    return path;
  }

  private static string[] ReadLabels(NodeExecutionContext context) {
    string relative = context.GetParameter("labels", "");
    if (string.IsNullOrWhiteSpace(relative)) {
      return [];
    }

    string path = context.ResolveProjectPath(relative);
    if (!File.Exists(path)) {
      throw new FileNotFoundException("VisionAI 标签文件不存在。", path);
    }

    return [.. File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0)];
  }

  private static string Label(IReadOnlyList<string> labels, int classId) {
    return classId >= 0 && classId < labels.Count ? labels[classId] : $"class {classId}";
  }

  private static int Provider(string value) {
    return value.ToUpperInvariant() switch { "AUTO" => 0, "CPU" => 1, "CUDA" => 2, _ => throw new ArgumentOutOfRangeException(nameof(value), value, "推理设备只能是 CPU、CUDA 或 Auto。") };
  }

  private static Rectangle ClampRectangle(float x, float y, float width, float height, Size size) {
    int left = Math.Clamp((int)Math.Round(x), 0, size.Width);
    int top = Math.Clamp((int)Math.Round(y), 0, size.Height);
    int right = Math.Clamp((int)Math.Round(x + width), 0, size.Width);
    int bottom = Math.Clamp((int)Math.Round(y + height), 0, size.Height);
    return Rectangle.FromLTRB(left, top, Math.Max(left, right), Math.Max(top, bottom));
  }

  private static void PutLabel(Mat image, Point point, string text) {
    CvInvoke.PutText(image, text, new Point(Math.Clamp(point.X, 0, Math.Max(0, image.Cols - 1)), Math.Clamp(Math.Max(18, point.Y - 4), 0, Math.Max(0, image.Rows - 1))), FontFace.HersheySimplex, .55, new MCvScalar(20, 230, 255), 2);
  }

  private static string DecodeText(byte[] buffer, uint offset, uint length) {
    if ((ulong)offset + length > (ulong)buffer.Length) {
      throw new InvalidDataException("VisionAI OCR 文本范围无效。");
    }

    return Encoding.UTF8.GetString(buffer, (int)offset, (int)length);
  }

  private static T[] MarshalArray<T>(nint pointer, int count) where T : struct {
    var result = new T[count];
    int size = Marshal.SizeOf<T>();
    for (int i = 0; i < count; i++) {
      result[i] = Marshal.PtrToStructure<T>(pointer + i * size);
    }

    return result;
  }

  private static nint Allocate<T>(nuint count) where T : struct {
    return AllocateBytes(checked(count * (nuint)Marshal.SizeOf<T>()));
  }

  private static nint AllocateBytes(nuint bytes) {
    return bytes == 0 ? 0 : Marshal.AllocHGlobal(checked((nint)bytes));
  }

  private static void Free(nint pointer) {
    if (pointer != 0) {
      Marshal.FreeHGlobal(pointer);
    }
  }
  private static void EnsureCount(string operation, nuint expected, nuint actual) {
    if (actual > expected) {
      throw new InvalidDataException($"VisionAI {operation}返回数量 {actual} 超过已查询容量 {expected}。");
    }
  }
}
