using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Tests;

public sealed class DrawOverlayTests
{
    private readonly OperatorRegistry _registry = OpenCvOperatorCatalog.CreateRegistry();

    [Fact]
    public async Task OverlayEllipse_DrawsRedEllipseOnTheBaseImage()
    {
        using var harness = new OverlayHarness(_registry);

        using var result = await harness.RunAsync("Draw.OverlayEllipse");

        using var output = result.GetOutput<Mat>("overlay", "Image");
        Assert.Equal(3, output.NumberOfChannels);
        Assert.True(RedPixelCount(output) > 50, "expected a red ellipse outline on the base image");
    }

    [Fact]
    public async Task OverlayContours_DrawsRedContoursOnTheBaseImage()
    {
        using var harness = new OverlayHarness(_registry);

        using var result = await harness.RunAsync("Draw.OverlayContours");

        using var output = result.GetOutput<Mat>("overlay", "Image");
        Assert.Equal(3, output.NumberOfChannels);
        Assert.True(RedPixelCount(output) > 50, "expected red contour pixels on the base image");
    }

    [Fact]
    public async Task OverlayEllipse_RejectsAWrongGeometryInput()
    {
        using var harness = new OverlayHarness(_registry);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.RunAsync("Draw.OverlayEllipse", ellipseValue: 42));

        Assert.Contains("拟合椭圆结果", error.Message);
    }

    private static int RedPixelCount(Mat bgr) {
        using var mask = new Mat();
        CvInvoke.InRange(bgr, new ScalarArray(new MCvScalar(0, 0, 160)), new ScalarArray(new MCvScalar(90, 90, 255)), mask);
        return CvInvoke.CountNonZero(mask);
    }

    /// <summary>Small workflow: base image plus externally supplied geometry, feeding one overlay node.</summary>
    private sealed class OverlayHarness : IDisposable {
        private readonly OperatorRegistry _registry;
        private readonly Mat _base;
        private readonly Mat _mask;
        private readonly VisionContours _contours;
        private readonly RotatedRect _ellipse;

        public OverlayHarness(OperatorRegistry registry) {
            _registry = registry;
            _base = new Mat(200, 200, DepthType.Cv8U, 3);
            _base.SetTo(new MCvScalar(40, 40, 40));
            _mask = new Mat(200, 200, DepthType.Cv8U, 1);
            _mask.SetTo(new MCvScalar(0));
            CvInvoke.Circle(_mask, new Point(100, 100), 60, new MCvScalar(255), 3);
            var items = new List<Point[]>();
            using var vectors = new VectorOfVectorOfPoint();
            CvInvoke.FindContours(_mask, vectors, null, RetrType.External, ChainApproxMethod.ChainApproxSimple);
            for (int index = 0; index < vectors.Size; index++) {
                items.Add(vectors[index].ToArray());
            }

            _contours = new VisionContours(items);
            using var first = new VectorOfPoint(items[0]);
            _ellipse = CvInvoke.FitEllipse(first);
        }

        public async Task<WorkflowExecutionResult> RunAsync(string overlayType, object? ellipseValue = null) {
            var source = new WorkflowNode { Id = "src", Type = "Image.Load", Name = "底图" };
            var contours = new WorkflowNode { Id = "cont", Type = "Blob.Contours", Name = "轮廓" };
            var fit = new WorkflowNode { Id = "fit", Type = "Contour.FitEllipse", Name = "拟合椭圆" };
            var overlay = new WorkflowNode { Id = "overlay", Type = overlayType, Name = "叠加" };

            // Externally supplied nodes still need their own inputs wired so validation passes.
            var connections = new List<WorkflowConnection> {
                new(source.Id, "Image", overlay.Id, "Image"),
                new(source.Id, "Image", contours.Id, "Image"),
                new(contours.Id, "Image", fit.Id, "Image"),
                new(contours.Id, "Contours", fit.Id, "Contours")
            };
            if (overlayType == "Draw.OverlayEllipse") {
                connections.Add(new(fit.Id, "Result", overlay.Id, "Ellipse"));
            } else {
                connections.Add(new(contours.Id, "Contours", overlay.Id, "Contours"));
            }

            var workflow = new WorkflowDocument { Nodes = { source, contours, fit, overlay } };
            foreach (var connection in connections) {
                workflow.Connections.Add(connection);
            }

            var external = new List<WorkflowExternalOutput> {
                new(source.Id, "Image", _base.Clone()),
                new(contours.Id, "Image", _mask.Clone()),
                new(contours.Id, "Contours", _contours),
                new(contours.Id, "Count", _contours.Items.Count),
                new(fit.Id, "Image", _mask.Clone()),
                new(fit.Id, "Result", ellipseValue ?? _ellipse)
            };

            return await new WorkflowExecutor(_registry).ExecuteAsync(workflow, overlay.Id, external);
        }

        public void Dispose() {
            _base.Dispose();
            _mask.Dispose();
        }
    }
}
