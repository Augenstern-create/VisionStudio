using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VisionStudio.Ai.Abstractions;

namespace VisionStudio.App;

/// <summary>
/// Minimal line chart for a parameter sweep: value on X, the first numeric metric on Y.
/// Drawn directly so it needs no charting dependency.
/// </summary>
public sealed class SweepChartControl : Control {
  private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#4C9AFF"));
  private static readonly IBrush PointBrush = new SolidColorBrush(Color.Parse("#4C9AFF"));
  private static readonly IBrush AxisBrush = new SolidColorBrush(Color.Parse("#60808080"));
  private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#C8C8C8"));

  public SweepChartControl(AiSweepSeries series) {
    Series = series;
    Width = 280;
    Height = 130;
    Margin = new Thickness(0, 2, 0, 2);
  }

  public AiSweepSeries Series { get; }

  public override void Render(DrawingContext context) {
    base.Render(context);
    var points = Series.Points
        .Select(point => (Value: ParseValue(point.Value), Metric: point.Metrics.TryGetValue(Series.MetricName, out double metric) ? metric : double.NaN))
        .Where(point => !double.IsNaN(point.Value) && !double.IsNaN(point.Metric))
        .ToList();
    if (points.Count == 0) {
      return;
    }

    const double left = 34;
    const double right = 10;
    const double top = 10;
    const double bottom = 22;
    double width = Bounds.Width - left - right;
    double height = Bounds.Height - top - bottom;
    if (width <= 4 || height <= 4) {
      return;
    }

    double minX = points.Min(point => point.Value);
    double maxX = points.Max(point => point.Value);
    double minY = points.Min(point => point.Metric);
    double maxY = points.Max(point => point.Metric);
    if (Math.Abs(maxX - minX) < 1e-9) {
      maxX = minX + 1;
    }

    if (Math.Abs(maxY - minY) < 1e-9) {
      maxY = minY + 1;
    }

    double MapX(double value) => left + (value - minX) / (maxX - minX) * width;
    double MapY(double value) => top + height - (value - minY) / (maxY - minY) * height;

    context.DrawLine(new Pen(AxisBrush, 1), new Point(left, top), new Point(left, top + height));
    context.DrawLine(new Pen(AxisBrush, 1), new Point(left, top + height), new Point(left + width, top + height));

    var geometry = new StreamGeometry();
    using (var sink = geometry.Open()) {
      bool first = true;
      foreach (var point in points) {
        var position = new Point(MapX(point.Value), MapY(point.Metric));
        if (first) {
          sink.BeginFigure(position, isFilled: false);
          first = false;
        } else {
          sink.LineTo(position);
        }
      }

      sink.EndFigure(isClosed: false);
    }

    context.DrawGeometry(null, new Pen(LineBrush, 1.5), geometry);
    foreach (var point in points) {
      context.DrawEllipse(PointBrush, null, new Point(MapX(point.Value), MapY(point.Metric)), 2.6, 2.6);
    }

    DrawLabel(context, Series.MetricName, 2, 0);
    DrawLabel(context, maxY.ToString(Format(maxY), CultureInfo.InvariantCulture), 2, top - 2);
    DrawLabel(context, minY.ToString(Format(minY), CultureInfo.InvariantCulture), 2, top + height - 12);
    DrawLabel(context, minX.ToString(Format(minX), CultureInfo.InvariantCulture), left - 2, top + height + 4);
    DrawLabel(context, maxX.ToString(Format(maxX), CultureInfo.InvariantCulture), left + width - 24, top + height + 4);
  }

  private static void DrawLabel(DrawingContext context, string text, double x, double y) {
    var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI, Noto Sans CJK SC, sans-serif"), 9.5, TextBrush);
    context.DrawText(formatted, new Point(x, y));
  }

  private static string Format(double value) {
    return Math.Abs(value) >= 100 ? "F0" : "0.##";
  }

  private static double ParseValue(string value) {
    return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : double.NaN;
  }
}
