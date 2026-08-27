using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using VisionStudio.Core;

namespace VisionStudio.Operators.OpenCv;

public sealed class CrackMeasurementOperator : IVisionOperator {
  public ValueTask ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) {
    var source = context.GetInput<Mat>("Image");
    using var gray = ToGray(source);
    using var smooth = new Mat();
    CvInvoke.GaussianBlur(gray, smooth, new Size(3, 3), 0);
    using var image = smooth.ToImage<Gray, byte>();
    double minimumContrast = context.GetParameter("minimumContrast", 30d);
    var seams = FindHorizontalDarkSeams(
        image.Data, image.Rows, image.Cols,
        context.GetParameter("contrastDistance", 5), minimumContrast,
        context.GetParameter("maxStep", 4), context.GetParameter("maxCracks", 8),
        context.GetParameter("exclusionRadius", 8), context.GetParameter("minLength", 80d), cancellationToken);

    var output = ToColor(source);
    var palette = new[] { new MCvScalar(0, 0, 255), new MCvScalar(0, 220, 255), new MCvScalar(255, 100, 0) };
    for (int index = 0; index < seams.Count; index++) {
      var color = palette[index % palette.Length];
      for (int pointIndex = 1; pointIndex < seams[index].Points.Count; pointIndex++) {
        CvInvoke.Line(output, seams[index].Points[pointIndex - 1], seams[index].Points[pointIndex], color, 2, LineType.AntiAlias);
      }

      var anchor = seams[index].Points[0];
      CvInvoke.PutText(output, $"#{index + 1}", new Point(anchor.X + 4, Math.Max(14, anchor.Y - 6)), FontFace.HersheySimplex, .45, color, 1);
    }

    double totalLength = seams.Sum(seam => seam.Length);
    CvInvoke.PutText(output, $"Cracks: {seams.Count}  Length: {totalLength:F1}px", new Point(8, 20), FontFace.HersheySimplex, .5, new MCvScalar(0, 0, 255), 1);
    string summary = seams.Count == 0
        ? $"未检测到平均局部对比度不低于 {minimumContrast:F1} 的横向裂纹。"
        : $"检测到 {seams.Count} 条横向裂纹，路径像素总长度 {totalLength:F1}px。";
    context.SetOutput("Image", output);
    context.SetOutput("CrackCount", seams.Count);
    context.SetOutput("PixelLength", totalLength);
    context.SetOutput("Summary", summary);
    return ValueTask.CompletedTask;
  }

  private sealed record Seam(IReadOnlyList<Point> Points, double Length, double Contrast);

  private static List<Seam> FindHorizontalDarkSeams(
      byte[,,] pixels, int rows, int columns, int contrastDistance, double minimumContrast,
      int maximumStep, int maximumCracks, int exclusionRadius, double minimumLength,
      CancellationToken cancellationToken) {
    contrastDistance = Math.Clamp(contrastDistance, 1, Math.Max(1, (rows - 1) / 2));
    maximumStep = Math.Clamp(maximumStep, 1, 20);
    maximumCracks = Math.Clamp(maximumCracks, 1, 100);
    exclusionRadius = Math.Clamp(exclusionRadius, 1, Math.Max(1, rows / 3));
    int margin = Math.Max(contrastDistance + 1, Math.Min(20, rows / 6));
    const double invalid = -1_000_000d;
    double[,] contrast = new double[rows, columns];
    for (int y = 0; y < rows; y++) {
      for (int x = 0; x < columns; x++) {
        contrast[y, x] = y < margin || y >= rows - margin
            ? invalid
            : (pixels[y - contrastDistance, x, 0] + pixels[y + contrastDistance, x, 0]) / 2d - pixels[y, x, 0];
      }
    }

    var result = new List<Seam>();
    for (int crackIndex = 0; crackIndex < maximumCracks; crackIndex++) {
      cancellationToken.ThrowIfCancellationRequested();
      var seam = TraceBestSeam(contrast, rows, columns, maximumStep, cancellationToken);
      if (seam.Contrast < minimumContrast || seam.Length < minimumLength) {
        break;
      }

      result.Add(seam);
      foreach (var point in seam.Points) {
        for (int y = Math.Max(0, point.Y - exclusionRadius); y <= Math.Min(rows - 1, point.Y + exclusionRadius); y++) {
          contrast[y, point.X] = invalid;
        }
      }
    }
    return result;
  }

  private static Seam TraceBestSeam(double[,] contrast, int rows, int columns, int maximumStep, CancellationToken cancellationToken) {
    const double invalid = -1_000_000d;
    const double stepPenalty = .8d;
    double[] previous = new double[rows];
    double[] current = new double[rows];
    sbyte[,] backtrack = new sbyte[columns, rows];
    for (int y = 0; y < rows; y++) {
      previous[y] = contrast[y, 0];
    }

    for (int x = 1; x < columns; x++) {
      if ((x & 31) == 0) {
        cancellationToken.ThrowIfCancellationRequested();
      }

      for (int y = 0; y < rows; y++) {
        double best = invalid;
        int bestStep = 0;
        for (int step = -maximumStep; step <= maximumStep; step++) {
          int sourceY = y + step;
          if (sourceY < 0 || sourceY >= rows) {
            continue;
          }

          double candidate = previous[sourceY] - Math.Abs(step) * stepPenalty;
          if (candidate <= best) {
            continue;
          }

          best = candidate;
          bestStep = step;
        }
        current[y] = best + contrast[y, x];
        backtrack[x, y] = (sbyte)bestStep;
      }
      (previous, current) = (current, previous);
    }

    int endY = 0;
    for (int y = 1; y < rows; y++) {
      if (previous[y] > previous[endY]) {
        endY = y;
      }
    }

    var points = new Point[columns];
    int currentY = endY;
    for (int x = columns - 1; x >= 0; x--) {
      points[x] = new Point(x, currentY);
      if (x > 0) {
        currentY += backtrack[x, currentY];
      }
    }
    double length = 0d;
    double averageContrast = 0d;
    for (int x = 0; x < columns; x++) {
      averageContrast += contrast[points[x].Y, x];
      if (x > 0) {
        length += Math.Sqrt(1 + Math.Pow(points[x].Y - points[x - 1].Y, 2));
      }
    }
    return new(points, length, averageContrast / columns);
  }

  private static Mat ToGray(Mat source) {
    var output = new Mat();
    if (source.NumberOfChannels == 1) {
      source.CopyTo(output);
    } else {
      CvInvoke.CvtColor(source, output, source.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);
    }

    return output;
  }

  private static Mat ToColor(Mat source) {
    var output = new Mat();
    if (source.NumberOfChannels == 1) {
      CvInvoke.CvtColor(source, output, ColorConversion.Gray2Bgr);
    } else {
      source.CopyTo(output);
    }

    return output;
  }
}
