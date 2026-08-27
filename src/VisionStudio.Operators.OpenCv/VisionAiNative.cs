using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VisionStudio.Operators.OpenCv;

internal sealed class VisionAiNative {
  private const int BufferTooSmall = 6;
  private static readonly ConcurrentDictionary<string, VisionAiNative> Loaded = new(StringComparer.OrdinalIgnoreCase);
  private readonly List<nint> libraryHandles = [];

  private VisionAiNative(string directory) {
    Directory = directory;
    LoadOptional("onnxruntime.dll");
    LoadOptional("onnxruntime_providers_shared.dll");
    string sdkPath = Path.Combine(directory, "vision_ai.dll");
    if (!File.Exists(sdkPath)) {
      throw new FileNotFoundException("VisionAI 插件缺少 vision_ai.dll。", sdkPath);
    }

    Library = NativeLibrary.Load(sdkPath);
    libraryHandles.Add(Library);

    ContextOptionsInit = Export<ContextOptionsInitDelegate>("vai_context_options_init");
    ContextCreate = Export<ContextCreateDelegate>("vai_context_create");
    ContextDestroy = Export<ContextDestroyDelegate>("vai_context_destroy");
    DetectorOptionsInit = Export<DetectorOptionsInitDelegate>("vai_detector_options_init");
    DetectorCreate = Export<DetectorCreateDelegate>("vai_detector_create");
    DetectorDestroy = Export<DetectorDestroyDelegate>("vai_detector_destroy");
    DetectorDetect = Export<DetectorDetectDelegate>("vai_detector_detect");
    SegmenterOptionsInit = Export<SegmenterOptionsInitDelegate>("vai_segmenter_options_init");
    SegmenterCreate = Export<SegmenterCreateDelegate>("vai_segmenter_create");
    SegmenterDestroy = Export<SegmenterDestroyDelegate>("vai_segmenter_destroy");
    SegmenterSegment = Export<SegmenterSegmentDelegate>("vai_segmenter_segment");
    OcrOptionsInit = Export<OcrOptionsInitDelegate>("vai_ocr_options_init");
    OcrCreate = Export<OcrCreateDelegate>("vai_ocr_create");
    OcrDestroy = Export<OcrDestroyDelegate>("vai_ocr_destroy");
    OcrRecognize = Export<OcrRecognizeDelegate>("vai_ocr_recognize");
    AnomalyOptionsInit = Export<AnomalyOptionsInitDelegate>("vai_anomaly_options_init");
    AnomalyCreate = Export<AnomalyCreateDelegate>("vai_anomaly_detector_create");
    AnomalyDestroy = Export<AnomalyDestroyDelegate>("vai_anomaly_detector_destroy");
    AnomalyDetect = Export<AnomalyDetectDelegate>("vai_anomaly_detector_detect");
    GetLastError = Export<GetLastErrorDelegate>("vai_get_last_error_message");
    VersionString = Export<VersionStringDelegate>("vai_version_string");
  }

  public string Directory { get; }
  public nint Library { get; }
  public ContextOptionsInitDelegate ContextOptionsInit { get; }
  public ContextCreateDelegate ContextCreate { get; }
  public ContextDestroyDelegate ContextDestroy { get; }
  public DetectorOptionsInitDelegate DetectorOptionsInit { get; }
  public DetectorCreateDelegate DetectorCreate { get; }
  public DetectorDestroyDelegate DetectorDestroy { get; }
  public DetectorDetectDelegate DetectorDetect { get; }
  public SegmenterOptionsInitDelegate SegmenterOptionsInit { get; }
  public SegmenterCreateDelegate SegmenterCreate { get; }
  public SegmenterDestroyDelegate SegmenterDestroy { get; }
  public SegmenterSegmentDelegate SegmenterSegment { get; }
  public OcrOptionsInitDelegate OcrOptionsInit { get; }
  public OcrCreateDelegate OcrCreate { get; }
  public OcrDestroyDelegate OcrDestroy { get; }
  public OcrRecognizeDelegate OcrRecognize { get; }
  public AnomalyOptionsInitDelegate AnomalyOptionsInit { get; }
  public AnomalyCreateDelegate AnomalyCreate { get; }
  public AnomalyDestroyDelegate AnomalyDestroy { get; }
  public AnomalyDetectDelegate AnomalyDetect { get; }
  public GetLastErrorDelegate GetLastError { get; }
  public VersionStringDelegate VersionString { get; }

  public static VisionAiNative Load() {
    string? environmentPath = Environment.GetEnvironmentVariable("VISIONSTUDIO_VISIONAI_RUNTIME");
    string? configuredPath = string.IsNullOrWhiteSpace(environmentPath) ? ReadConfiguredPath() : environmentPath;
    configuredPath = string.IsNullOrWhiteSpace(configuredPath) ? Path.Combine("plugins", "VisionAI") : configuredPath;
    string[] roots = CandidateRoots(configuredPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    string? directory = roots.Select(root => File.Exists(Path.Combine(root, "vision_ai.dll")) ? root : Path.Combine(root, "bin"))
        .FirstOrDefault(path => File.Exists(Path.Combine(path, "vision_ai.dll"))) ?? throw new DirectoryNotFoundException($"未找到 VisionAI 运行库。请安装插件到软件目录的 plugins/VisionAI，或设置 VISIONSTUDIO_VISIONAI_RUNTIME。已检查：{string.Join("；", roots)}");
    return Loaded.GetOrAdd(directory, static path => new VisionAiNative(path));
  }

  private static string? ReadConfiguredPath() {
    string file = Path.Combine(AppContext.BaseDirectory, "config", "visionai-runtime.json");
    if (!File.Exists(file)) {
      return null;
    }

    using var document = JsonDocument.Parse(File.ReadAllText(file));
    return document.RootElement.TryGetProperty("runtimePath", out var value) ? value.GetString() : null;
  }

  private static IEnumerable<string> CandidateRoots(string configuredPath) {
    if (Path.IsPathRooted(configuredPath)) {
      yield return Path.GetFullPath(configuredPath);
      yield break;
    }
    yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    yield return Path.GetFullPath(Path.Combine(System.IO.Directory.GetCurrentDirectory(), configuredPath));
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && directory is not null; i++, directory = directory.Parent) {
      yield return Path.GetFullPath(Path.Combine(directory.FullName, configuredPath));
    }
  }

  public string LastError(int status) {
    nint pointer = GetLastError();
    string? message = pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
    return string.IsNullOrWhiteSpace(message) ? $"VisionAI 调用失败，状态码 {status}。" : message;
  }

  public static void EnsureQueryStatus(VisionAiNative api, int status) {
    if (status is not 0 and not BufferTooSmall) {
      throw new InvalidOperationException(api.LastError(status));
    }
  }

  public static void EnsureSuccess(VisionAiNative api, int status) {
    if (status != 0) {
      throw new InvalidOperationException(api.LastError(status));
    }
  }

  private void LoadOptional(string fileName) {
    string path = Path.Combine(Directory, fileName);
    if (File.Exists(path)) {
      libraryHandles.Add(NativeLibrary.Load(path));
    }
  }

  private T Export<T>(string name) where T : Delegate {
    return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library, name));
  }

  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ContextOptionsInitDelegate(ref ContextOptions options);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ContextCreateDelegate(ref ContextOptions options, out nint context);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ContextDestroyDelegate(nint context);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DetectorOptionsInitDelegate(ref DetectorOptions options);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DetectorCreateDelegate(nint context, ref DetectorOptions options, out nint detector);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DetectorDestroyDelegate(nint detector);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DetectorDetectDelegate(nint detector, ref Image image, nint detections, nuint capacity, out nuint count);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void SegmenterOptionsInitDelegate(ref SegmenterOptions options);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SegmenterCreateDelegate(nint context, ref SegmenterOptions options, out nint segmenter);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void SegmenterDestroyDelegate(nint segmenter);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SegmenterSegmentDelegate(nint segmenter, ref Image image, nint segments, nuint segmentCapacity, nint masks, nuint maskCapacity, out nuint count, out nuint maskBytes);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void OcrOptionsInitDelegate(ref OcrOptions options);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OcrCreateDelegate(nint context, ref OcrOptions options, out nint ocr);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void OcrDestroyDelegate(nint ocr);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OcrRecognizeDelegate(nint ocr, ref Image image, nint results, nuint resultCapacity, nint text, nuint textCapacity, out nuint count, out nuint textBytes);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void AnomalyOptionsInitDelegate(ref AnomalyOptions options);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int AnomalyCreateDelegate(nint context, ref AnomalyOptions options, out nint detector);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void AnomalyDestroyDelegate(nint detector);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int AnomalyDetectDelegate(nint detector, ref Image image, ref AnomalyResult result, nint heatmap, nuint heatmapCapacity, nint mask, nuint maskCapacity, out nuint elements);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint GetLastErrorDelegate();
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint VersionStringDelegate();

  [InlineArray(16)] public struct Reserved16 { private uint element0; }
  [InlineArray(8)] public struct Reserved8 { private uint element0; }
  [InlineArray(4)] public struct Reserved4 { private uint element0; }

  [StructLayout(LayoutKind.Sequential)] public struct ContextOptions { public uint StructSize; public Reserved16 Reserved; }
  [StructLayout(LayoutKind.Sequential)] public struct Image { public uint StructSize; public nint Data; public int Width; public int Height; public nuint StrideBytes; public int PixelFormat; public Reserved8 Reserved; }
  [StructLayout(LayoutKind.Sequential)] public struct DetectorOptions { public uint StructSize; public nint ModelPath; public float ConfidenceThreshold; public float NmsIouThreshold; public int Provider; public int DeviceId; public Reserved16 Reserved; }
  [StructLayout(LayoutKind.Sequential)] public struct Detection { public float X; public float Y; public float Width; public float Height; public float Confidence; public int ClassId; }
  [StructLayout(LayoutKind.Sequential)] public struct SegmenterOptions { public uint StructSize; public nint ModelPath; public float ConfidenceThreshold; public float NmsIouThreshold; public float MaskThreshold; public int Provider; public int DeviceId; public Reserved16 Reserved; }
  [StructLayout(LayoutKind.Sequential)] public struct Segment { public float X; public float Y; public float Width; public float Height; public float Confidence; public int ClassId; public int MaskX; public int MaskY; public int MaskWidth; public int MaskHeight; public nuint MaskOffset; public nuint MaskStrideBytes; public Reserved4 Reserved; }
  [StructLayout(LayoutKind.Sequential)] public struct OcrOptions { public uint StructSize; public nint DetectionModelPath; public nint RecognitionModelPath; public nint CharacterDictPath; public float DetectionThreshold; public float BoxThreshold; public float UnclipRatio; public int Provider; public int DeviceId; public Reserved16 Reserved; }
  [StructLayout(LayoutKind.Sequential)] public struct OcrResult { public float X0; public float Y0; public float X1; public float Y1; public float X2; public float Y2; public float X3; public float Y3; public float Confidence; public uint TextOffset; public uint TextLength; public Reserved4 Reserved; }
  [StructLayout(LayoutKind.Sequential)] public struct AnomalyOptions { public uint StructSize; public nint ModelPath; public int InputWidth; public int InputHeight; public float ImageThreshold; public float PixelThreshold; public int Preprocess; public int Provider; public int DeviceId; public Reserved16 Reserved; }
  [StructLayout(LayoutKind.Sequential)] public struct AnomalyResult { public float Score; public int Anomalous; public int MapWidth; public int MapHeight; public Reserved8 Reserved; }
}
