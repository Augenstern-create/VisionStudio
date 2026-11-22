using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Emgu.CV;
using Emgu.CV.CvEnum;
using VisionStudio.Core;

namespace VisionStudio.Operators.OpenCv;

public sealed record BatchOutputValue(string Port, string Summary);

public sealed record BatchItemResult(
    string File,
    bool Success,
    TimeSpan Duration,
    IReadOnlyList<BatchOutputValue> Outputs,
    string? Error);

/// <summary>
/// Runs one workflow target node over a folder of images without touching the workflow itself:
/// each file is decoded and injected as the image input node's external output, so images may
/// live outside the project directory.
/// </summary>
public sealed class BatchWorkflowRunner(OperatorRegistry registry, string projectRoot) {
  public async Task<BatchItemResult> RunOneAsync(
      WorkflowDocument workflow,
      string inputNodeId,
      string targetNodeId,
      string filePath,
      CancellationToken cancellationToken = default) {
    var stopwatch = Stopwatch.StartNew();
    Mat? image = null;
    try {
      cancellationToken.ThrowIfCancellationRequested();
      image = OpenCvFileCodec.Read(filePath, ImreadModes.Unchanged);
      if (image.IsEmpty) {
        image?.Dispose();
        return new(Path.GetFileName(filePath), false, stopwatch.Elapsed, [], "无法解码该图片 / Image could not be decoded.");
      }

      // The executor takes ownership of injected values and disposes them with the result.
      using var result = await new WorkflowExecutor(registry, projectRoot).ExecuteAsync(
          workflow, targetNodeId, [new WorkflowExternalOutput(inputNodeId, "Image", image)], cancellationToken);
      stopwatch.Stop();
      registry.TryGetDescriptor(workflow.Nodes.Single(node => node.Id == targetNodeId).Type, out var descriptor);
      var values = result.GetNodeOutputs(targetNodeId);
      var outputs = descriptor.Outputs
          .Where(port => values.ContainsKey(port.Name))
          .Select(port => new BatchOutputValue(port.Name, Summarize(values[port.Name])))
          .ToList();
      return new(Path.GetFileName(filePath), true, stopwatch.Elapsed, outputs, null);
    } catch (OperationCanceledException) {
      SafeDispose(image);
      throw;
    } catch (Exception exception) {
      SafeDispose(image);
      stopwatch.Stop();
      return new(Path.GetFileName(filePath), false, stopwatch.Elapsed, [], Describe(exception));
    }
  }

  public async Task<IReadOnlyList<BatchItemResult>> RunAsync(
      WorkflowDocument workflow,
      string inputNodeId,
      string targetNodeId,
      IReadOnlyList<string> files,
      IProgress<BatchItemResult>? progress = null,
      CancellationToken cancellationToken = default) {
    var results = new List<BatchItemResult>(files.Count);
    foreach (string file in files) {
      cancellationToken.ThrowIfCancellationRequested();
      var result = await RunOneAsync(workflow, inputNodeId, targetNodeId, file, cancellationToken);
      results.Add(result);
      progress?.Report(result);
    }

    return results;
  }

  private static readonly HashSet<string> SupportedExtensions =
      new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" };

  public static IReadOnlyList<string> CollectImages(string directory, bool recursive = false) {
    return [.. Directory
        .EnumerateFiles(directory, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
        .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
  }

  private static string Summarize(object value) {
    return value switch {
      Mat image => $"{image.Width}x{image.Height}, {image.NumberOfChannels} channel(s)",
      string text => text,
      ICollection collection => $"Collection({collection.Count})",
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      _ => value.ToString() ?? value.GetType().Name
    };
  }

  private static string Describe(Exception exception) {
    return exception is WorkflowValidationException validation
        ? string.Join("; ", validation.Errors)
        : exception.Message;
  }

  private static void SafeDispose(Mat? image) {
    try { image?.Dispose(); } catch (Exception) { }
  }
}

public static class BatchResultCsv {
  /// <summary>Writes results as UTF-8 with BOM so spreadsheet tools keep Chinese text readable.</summary>
  public static async Task WriteAsync(
      string path,
      IReadOnlyList<string> outputPorts,
      IReadOnlyList<BatchItemResult> results,
      CancellationToken cancellationToken = default) {
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    var builder = new StringBuilder();
    builder.Append("file,success,duration_ms,error");
    foreach (string port in outputPorts) {
      builder.Append(',').Append(Escape(port));
    }

    builder.AppendLine();
    foreach (var result in results) {
      cancellationToken.ThrowIfCancellationRequested();
      builder.Append(Escape(result.File))
          .Append(',').Append(result.Success ? "true" : "false")
          .Append(',').Append(result.Duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture))
          .Append(',').Append(Escape(result.Error ?? ""));
      var values = result.Outputs.ToDictionary(output => output.Port, output => output.Summary, StringComparer.Ordinal);
      foreach (string port in outputPorts) {
        builder.Append(',').Append(Escape(values.TryGetValue(port, out string? summary) ? summary : ""));
      }

      builder.AppendLine();
    }

    await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
  }

  private static string Escape(string value) {
    return value.Contains(',') || value.Contains('"') || value.Contains('\n')
        ? "\"" + value.Replace("\"", "\"\"") + "\""
        : value;
  }
}
