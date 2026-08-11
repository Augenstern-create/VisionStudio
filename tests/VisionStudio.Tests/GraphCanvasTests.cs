using VisionStudio.App;
using VisionStudio.Operators.OpenCv;
using VisionStudio.Core;

namespace VisionStudio.Tests;

public sealed class GraphCanvasTests
{
    [Fact]
    public void ConnectionHitTest_UsesWideNearestDistanceBand()
    {
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = WorkflowTests.LinearWorkflow(registry);
        workflow.Nodes[0].X = 40; workflow.Nodes[0].Y = 100;
        workflow.Nodes[1].X = 260; workflow.Nodes[1].Y = 100;
        workflow.Nodes[2].X = 480; workflow.Nodes[2].Y = 100;
        var canvas = new GraphCanvas { Registry = registry, Workflow = workflow };

        // First edge is horizontal from x=224 to x=260 at y=156. The click is
        // intentionally 15 px away and must still select because the UI uses an
        // 18 px interaction band around the visually thin line.
        Assert.Equal(workflow.Connections[0], canvas.HitTestConnection(242, 171));
        Assert.Null(canvas.HitTestConnection(242, 180));
    }

    [Fact]
    public void FindAvailableNodePosition_AvoidsExistingNodesAndTracksPan()
    {
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = WorkflowTests.LinearWorkflow(registry);
        workflow.Nodes[0].X = 48; workflow.Nodes[0].Y = 48;
        workflow.Nodes[1].X = 48; workflow.Nodes[1].Y = 248;
        workflow.Nodes[2].X = 48; workflow.Nodes[2].Y = 448;
        var canvas = new GraphCanvas { Registry = registry, Workflow = workflow, Width = 800, Height = 600 };

        var beforePan = canvas.FindAvailableNodePosition();
        Assert.DoesNotContain(workflow.Nodes, node => new Avalonia.Rect(node.X, node.Y, 184, 170).Inflate(16).Contains(beforePan));
        canvas.PanBy(-320, -160);
        var afterPan = canvas.FindAvailableNodePosition();
        Assert.Equal(new Avalonia.Vector(-320, -160), canvas.PanOffset);
        Assert.True(afterPan.X + canvas.PanOffset.X >= 24);
        Assert.True(afterPan.Y + canvas.PanOffset.Y >= 24);
        Assert.DoesNotContain(workflow.Nodes, node => new Avalonia.Rect(node.X, node.Y, 184, 170).Inflate(16).Contains(afterPan));
    }

    [Fact]
    public void AutoLayout_ArrangesDagByLevelAndSeparatesRows()
    {
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = WorkflowTests.LinearWorkflow(registry);
        var secondSource = OpenCvOperatorCatalog.CreateNode(registry.Descriptors.Single(x => x.Type == "Image.Load"), 0, 0);
        workflow.Nodes.Add(secondSource);
        foreach (var node in workflow.Nodes) { node.X = 10; node.Y = 10; }
        var canvas = new GraphCanvas { Registry = registry, Workflow = workflow, Width = 1000, Height = 700 };
        var changing = 0; var changed = 0;
        canvas.WorkflowChanging += (_, _) => changing++;
        canvas.WorkflowChanged += (_, _) => changed++;

        Assert.True(canvas.AutoLayout());
        Assert.Equal(1, changing);
        Assert.Equal(1, changed);
        foreach (var edge in workflow.Connections)
        {
            var from = workflow.Nodes.Single(node => node.Id == edge.FromNode);
            var to = workflow.Nodes.Single(node => node.Id == edge.ToNode);
            Assert.True(from.X < to.X);
        }
        var sources = workflow.Nodes.Where(node => workflow.Connections.All(edge => edge.ToNode != node.Id)).ToList();
        Assert.Equal(sources[0].X, sources[1].X);
        Assert.NotEqual(sources[0].Y, sources[1].Y);
    }
}
