using System.Text.Json;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Tests;

public sealed class WorkflowAutomationTests
{
    [Fact]
    public async Task ApplyAsync_CommitsBatchPersistsAndIsIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temporary.Path, "AI API");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var session = new WorkflowAutomationSession(project, new WorkflowDocument(), projects, registry, new StubRunner());
        WorkflowChangedEventArgs? changed = null;
        session.WorkflowChanged += (_, args) => changed = args;

        var request = new WorkflowMutationRequest(
            "request-1",
            ExpectedRevision: 0,
            Edits:
            [
                new AddNodeEdit("load", "Image.Load", Parameters: new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement("images/input/part.png")
                }),
                new AddNodeEdit("gray", "Image.Gray"),
                new AddNodeEdit("threshold", "Image.Threshold", Parameters: new Dictionary<string, JsonElement>
                {
                    ["threshold"] = JsonSerializer.SerializeToElement(96d)
                }),
                new ConnectEdit("load", "Image", "gray", "Image"),
                new ConnectEdit("gray", "Image", "threshold", "Image")
            ],
            Summary: "Build a threshold pipeline",
            SelectNodeId: "threshold");

        var first = await session.ApplyAsync(request);
        var duplicate = await session.ApplyAsync(request);
        var persisted = await projects.LoadWorkflowAsync(project);

        Assert.True(first.Applied);
        Assert.Equal(1, first.Revision);
        Assert.Equal(first, duplicate);
        Assert.Equal(3, persisted.Nodes.Count);
        Assert.Equal(2, persisted.Connections.Count);
        Assert.Equal("threshold", changed?.SelectedNodeId);
        Assert.Equal(96d, persisted.Nodes.Single(node => node.Id == "threshold").GetParameter("threshold", -1d));

        session.SynchronizeFromHost(changed!.Workflow, incrementRevision: false);
        changed.Workflow.Name = "Edited by user after AI";
        session.NotifyHostChanged();
        var synchronized = await session.GetSnapshotAsync();
        Assert.Equal(2, synchronized.Revision);
        Assert.Equal("Edited by user after AI", synchronized.Workflow.Name);
    }

    [Fact]
    public async Task ApplyAsync_RejectsStaleOrInvalidBatchWithoutChangingProject()
    {
        using var temporary = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temporary.Path, "AI API");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var session = new WorkflowAutomationSession(project, new WorkflowDocument(), projects, registry, new StubRunner());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ApplyAsync(new(
            "invalid", 0,
            [new AddNodeEdit("threshold", "Image.Threshold", Parameters: new Dictionary<string, JsonElement>
            {
                ["threshold"] = JsonSerializer.SerializeToElement(999d)
            })])));
        Assert.Empty((await session.GetSnapshotAsync()).Workflow.Nodes);

        session.NotifyHostChanged();
        var conflict = await Assert.ThrowsAsync<WorkflowRevisionConflictException>(() => session.ApplyAsync(new(
            "stale", 0, [new AddNodeEdit("load", "Image.Load")])));
        Assert.Equal(0, conflict.ExpectedRevision);
        Assert.Equal(1, conflict.ActualRevision);
        Assert.Empty((await projects.LoadWorkflowAsync(project)).Nodes);
    }

    [Fact]
    public async Task ExecuteAsync_PublishesSerializableResultAndEvent()
    {
        using var temporary = new TemporaryDirectory();
        var projects = new ProjectService();
        var project = await projects.CreateAsync(temporary.Path, "AI API");
        var registry = OpenCvOperatorCatalog.CreateRegistry();
        var workflow = new WorkflowDocument
        {
            Nodes = { OpenCvOperatorCatalog.CreateNode(registry.Descriptors.Single(item => item.Type == "Image.Load"), 0, 0) }
        };
        workflow.Nodes[0] = new WorkflowNode { Id = "load", Type = "Image.Load", Name = "加载图像" };
        workflow.Nodes[0].SetParameter("path", "images/input/part.png");
        var session = new WorkflowAutomationSession(project, workflow, projects, registry, new StubRunner());
        WorkflowAutomationRunResult? published = null;
        session.ExecutionCompleted += (_, args) => published = args.Result;

        var result = await session.ExecuteAsync("run-1", "load");
        var json = JsonSerializer.Serialize(result, WorkflowJson.Options);
        var editJson = JsonSerializer.Serialize<WorkflowEdit>(new AddNodeEdit("gray", "Image.Gray"), WorkflowJson.Options);

        Assert.True(result.Success);
        Assert.Equal(result, published);
        Assert.Contains("measurement", json);
        Assert.Contains("\"kind\": \"addNode\"", editJson);
    }

    private sealed class StubRunner : IWorkflowAutomationRunner
    {
        public Task<WorkflowAutomationRunResult> RunAsync(string requestId, ProjectHandle project, WorkflowDocument workflow, string targetNodeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowAutomationRunResult(
                requestId,
                targetNodeId,
                true,
                [new WorkflowAutomationOutput("measurement", NodeValueType.Double, "42")],
                [new NodeTiming(targetNodeId, TimeSpan.FromMilliseconds(1))]));
    }
}
