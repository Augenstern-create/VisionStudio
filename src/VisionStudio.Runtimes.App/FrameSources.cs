using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Runtimes.App;

public sealed class CapturedFrame(
    Mat image,
    long sequence,
    DateTimeOffset capturedAt,
    string sourceName) : IDisposable {
  public Mat Image { get; } = image;
  public long Sequence { get; } = sequence;
  public DateTimeOffset CapturedAt { get; } = capturedAt;
  public string SourceName { get; } = sourceName;
  public void Dispose() {
    Image.Dispose();
  }
}

public interface IFrameSource : IAsyncDisposable {
  string DisplayName { get; }
  ValueTask OpenAsync(CancellationToken cancellationToken = default);
  ValueTask<CapturedFrame> CaptureAsync(CancellationToken cancellationToken = default);
}

public sealed class FrameSourceFactory {
  public IFrameSource Create(RuntimeSourceSettings settings, string projectRoot) {
    return settings.Kind switch {
      RuntimeSourceKind.Image => new ImageFrameSource(
          ResolveProjectFile(projectRoot, settings.Path)),
      RuntimeSourceKind.ImageFolder => new FolderFrameSource(
          ResolveProjectDirectory(projectRoot, settings.Path),
          settings.Loop),
      RuntimeSourceKind.Video => new VideoFrameSource(
          ResolveProjectFile(projectRoot, settings.Path),
          settings.Loop),
      RuntimeSourceKind.Camera => new EmguCameraFrameSource(
          settings.CameraIndex,
          settings.Width,
          settings.Height),
      RuntimeSourceKind.Simulated => new SimulatedFrameSource(
          settings.Width,
          settings.Height),
      _ => throw new NotSupportedException($"Unsupported frame source '{settings.Kind}'.")
    };
  }

  private static string ResolveProjectFile(string projectRoot, string relativePath) {
    string path = ProjectPaths.ResolveInside(projectRoot, relativePath);
    if (!File.Exists(path)) {
      throw new FileNotFoundException("Frame source file does not exist.", path);
    }
    return path;
  }

  private static string ResolveProjectDirectory(string projectRoot, string relativePath) {
    string path = ProjectPaths.ResolveInside(projectRoot, relativePath);
    if (!Directory.Exists(path)) {
      throw new DirectoryNotFoundException($"Frame source directory does not exist: {path}");
    }
    return path;
  }
}

public sealed class ImageFrameSource(string path) : IFrameSource {
  private long _sequence;
  public string DisplayName => Path.GetFileName(path);
  public ValueTask OpenAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.CompletedTask;
  }
  public ValueTask<CapturedFrame> CaptureAsync(
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    var image = OpenCvFileCodec.Read(path);
    if (image.IsEmpty) {
      image.Dispose();
      throw new InvalidDataException($"OpenCV could not decode '{path}'.");
    }
    return ValueTask.FromResult(new CapturedFrame(
        image,
        Interlocked.Increment(ref _sequence),
        DateTimeOffset.UtcNow,
        DisplayName));
  }
  public ValueTask DisposeAsync() {
    return ValueTask.CompletedTask;
  }
}

// A conventional constructor keeps SDK adapter fields explicit for later implementations.
#pragma warning disable IDE0290
public sealed class FolderFrameSource : IFrameSource {
  private static readonly string[] _extensions =
      [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp"];
  private string[] _files = [];
  private int _index;
  private readonly bool _loop;
  private readonly string _path;

  public FolderFrameSource(string path, bool loop) {
    _path = path;
    _loop = loop;
  }
#pragma warning restore IDE0290

  public string DisplayName => Path.GetFileName(_path.TrimEnd(
      Path.DirectorySeparatorChar,
      Path.AltDirectorySeparatorChar));
  public ValueTask OpenAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    _files = [.. Directory.EnumerateFiles(_path)
        .Where(file => _extensions.Contains(
            Path.GetExtension(file),
            StringComparer.OrdinalIgnoreCase))
        .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)];
    if (_files.Length == 0) {
      throw new InvalidDataException(
          $"Image folder contains no supported images: {_path}");
    }
    return ValueTask.CompletedTask;
  }
  public ValueTask<CapturedFrame> CaptureAsync(
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    if (_index >= _files.Length) {
      if (!_loop) {
        throw new EndOfStreamException("The image folder has no more frames.");
      }
      _index = 0;
    }
    string file = _files[_index++];
    var image = OpenCvFileCodec.Read(file);
    return ValueTask.FromResult(new CapturedFrame(
        image,
        _index,
        DateTimeOffset.UtcNow,
        Path.GetFileName(file)));
  }
  public ValueTask DisposeAsync() {
    return ValueTask.CompletedTask;
  }
}

public sealed class VideoFrameSource(string path, bool loop) : IFrameSource {
  private VideoCapture? _capture;
  private long _sequence;
  public string DisplayName => Path.GetFileName(path);
  public ValueTask OpenAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    _capture = new VideoCapture(path);
    if (!_capture.IsOpened) {
      DisposeCapture();
      throw new InvalidOperationException($"Could not open video: {path}");
    }
    return ValueTask.CompletedTask;
  }
  public ValueTask<CapturedFrame> CaptureAsync(
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    var capture = _capture
        ?? throw new InvalidOperationException("Video source is not open.");
    var image = new Mat();
    if (!capture.Read(image) || image.IsEmpty) {
      if (!loop) {
        image.Dispose();
        throw new EndOfStreamException("The video has no more frames.");
      }
      capture.Set(CapProp.PosFrames, 0);
      if (!capture.Read(image) || image.IsEmpty) {
        image.Dispose();
        throw new InvalidDataException($"Could not decode a video frame from '{path}'.");
      }
    }
    return ValueTask.FromResult(new CapturedFrame(
        image,
        Interlocked.Increment(ref _sequence),
        DateTimeOffset.UtcNow,
        DisplayName));
  }
  public ValueTask DisposeAsync() {
    DisposeCapture();
    return ValueTask.CompletedTask;
  }
  private void DisposeCapture() {
    _capture?.Dispose();
    _capture = null;
  }
}

public sealed class EmguCameraFrameSource(
    int cameraIndex,
    int width,
    int height) : IFrameSource {
  private VideoCapture? _capture;
  private long _sequence;
  public string DisplayName => $"EmguCV Camera {cameraIndex}";
  public ValueTask OpenAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    _capture = new VideoCapture(cameraIndex);
    if (width > 0) {
      _capture.Set(CapProp.FrameWidth, width);
    }
    if (height > 0) {
      _capture.Set(CapProp.FrameHeight, height);
    }
    if (!_capture.IsOpened) {
      DisposeCapture();
      throw new InvalidOperationException(
          $"Could not open EmguCV camera index {cameraIndex}.");
    }
    return ValueTask.CompletedTask;
  }
  public ValueTask<CapturedFrame> CaptureAsync(
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    var capture = _capture
        ?? throw new InvalidOperationException("Camera source is not open.");
    var image = new Mat();
    if (!capture.Read(image) || image.IsEmpty) {
      image.Dispose();
      throw new InvalidOperationException(
          $"Could not capture a frame from EmguCV camera index {cameraIndex}.");
    }
    return ValueTask.FromResult(new CapturedFrame(
        image,
        Interlocked.Increment(ref _sequence),
        DateTimeOffset.UtcNow,
        DisplayName));
  }
  public ValueTask DisposeAsync() {
    DisposeCapture();
    return ValueTask.CompletedTask;
  }
  private void DisposeCapture() {
    _capture?.Dispose();
    _capture = null;
  }
}

public sealed class SimulatedFrameSource(int width, int height) : IFrameSource {
  private long _sequence;
  public string DisplayName => "Simulated frame";
  public ValueTask OpenAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.CompletedTask;
  }
  public ValueTask<CapturedFrame> CaptureAsync(
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    int safeWidth = Math.Clamp(width, 64, 8192);
    int safeHeight = Math.Clamp(height, 64, 8192);
    long sequence = Interlocked.Increment(ref _sequence);
    var image = new Mat(safeHeight, safeWidth, DepthType.Cv8U, 3);
    image.SetTo(new MCvScalar(32, 32, 32));
    CvInvoke.Rectangle(
        image,
        new Rectangle(20, 20, safeWidth - 40, safeHeight - 40),
        new MCvScalar(90, 180, 60),
        3);
    CvInvoke.PutText(
        image,
        $"Vision Studio simulated #{sequence}",
        new Point(35, Math.Max(60, safeHeight / 2)),
        FontFace.HersheySimplex,
        0.8,
        new MCvScalar(255, 255, 255),
        2);
    return ValueTask.FromResult(new CapturedFrame(
        image,
        sequence,
        DateTimeOffset.UtcNow,
        DisplayName));
  }
  public ValueTask DisposeAsync() {
    return ValueTask.CompletedTask;
  }
}
