using VisionStudio.CodeGen;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Tests;

public sealed class WorkflowTests
{
    private readonly OperatorRegistry _registry = OpenCvOperatorCatalog.CreateRegistry();

    [Fact]
    public void Validate_ReturnsDeterministicTopologicalOrder()
    {
        var workflow = LinearWorkflow(_registry);
        var result = WorkflowValidator.Validate(workflow, _registry);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(workflow.Nodes.Select(x => x.Id), result.TopologicalOrder.Select(x => x.Id));
    }

    [Fact]
    public void Validate_RejectsCycleAndDuplicateInput()
    {
        var workflow = LinearWorkflow(_registry);
        var gray = workflow.Nodes[1];
        var threshold = workflow.Nodes[2];
        workflow.Connections.Add(new(threshold.Id, "Image", gray.Id, "Image"));
        var result = WorkflowValidator.Validate(workflow, _registry);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, x => x.Contains("more than one", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsMissingRequiredInput()
    {
        var descriptor = _registry.Descriptors.Single(x => x.Type == "Image.Threshold");
        var workflow = new WorkflowDocument { Nodes = { OpenCvOperatorCatalog.CreateNode(descriptor, 0, 0) } };
        var result = WorkflowValidator.Validate(workflow, _registry);
        Assert.Contains(result.Errors, x => x.Contains("not connected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AllowsOneOutputToFanOutButKeepsEachInputUnique()
    {
        WorkflowNode Add(string type)
        {
            _registry.TryGetDescriptor(type, out var descriptor);
            return OpenCvOperatorCatalog.CreateNode(descriptor, 0, 0);
        }
        var load = Add("Image.Load");
        var gray = Add("Image.Gray");
        var canny = Add("Edge.Canny");
        var workflow = new WorkflowDocument
        {
            Nodes = { load, gray, canny },
            Connections = { new(load.Id, "Image", gray.Id, "Image"), new(load.Id, "Image", canny.Id, "Image") }
        };

        var result = WorkflowValidator.Validate(workflow, _registry);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsDuplicateIdsUnknownPortsAndTypeMismatch()
    {
        var registry = new OperatorRegistry();
        registry.Register(new("Source", "Source", "Test", [], [new("Text", NodeValueType.String)], []), () => new NoOp());
        registry.Register(new("Sink", "Sink", "Test", [new("Image", NodeValueType.Mat)], [], []), () => new NoOp());
        var workflow = new WorkflowDocument
        {
            Nodes =
            {
                new WorkflowNode { Id = "same", Type = "Source" },
                new WorkflowNode { Id = "same", Type = "Source" },
                new WorkflowNode { Id = "sink", Type = "Sink" }
            },
            Connections = { new("same", "Text", "sink", "Image"), new("same", "Missing", "sink", "Other") }
        };
        var result = WorkflowValidator.Validate(workflow, registry);
        Assert.Contains(result.Errors, x => x.Contains("Duplicate node", StringComparison.Ordinal));
        Assert.Contains(result.Errors, x => x.Contains("Cannot connect", StringComparison.Ordinal));
        Assert.Contains(result.Errors, x => x.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_RejectsFutureSchema()
    {
        Assert.Throws<NotSupportedException>(() => WorkflowJson.Deserialize("{\"schemaVersion\":999,\"nodes\":[],\"connections\":[]}"));
    }

    [Fact]
    public void Json_RoundTripsParametersAndConnections()
    {
        var original = LinearWorkflow(_registry);
        var restored = WorkflowJson.Deserialize(WorkflowJson.Serialize(original));
        Assert.Equal(original.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(original.Connections, restored.Connections);
        Assert.Equal(128d, restored.Nodes[2].GetParameter("threshold", -1d));
    }

    [Fact]
    public void CodeGenerator_EmitsPlainEmguCvInTopologicalOrder()
    {
        var code = new CSharpWorkflowGenerator(_registry).Generate(LinearWorkflow(_registry), "Gear Inspection");
        Assert.Contains("public sealed class Gear_Inspection", code);
        Assert.Contains("CvInvoke.CvtColor", code);
        Assert.Contains("CvInvoke.Threshold", code);
        Assert.Contains("return mat_", code);
        Assert.DoesNotContain("VisionStudio", code);
        Assert.True(code.IndexOf("CvtColor", StringComparison.Ordinal) < code.IndexOf("Threshold", StringComparison.Ordinal));
    }

    [Fact]
    public void CodeGenerator_EmbedsExecutionManifestWithNodesConnectionsAndParameters()
    {
        var workflow = LinearWorkflow(_registry);
        var code = new CSharpWorkflowGenerator(_registry).Generate(workflow);

        var manifest = CSharpExportManifest.Parse(code);

        Assert.Equal(CSharpExportManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(workflow.Nodes[2].Id, manifest.TargetNodeId);
        Assert.Equal(3, manifest.Nodes.Count);
        Assert.Equal(2, manifest.Connections.Count);
        var threshold = manifest.Nodes.Single(node => node.Type == "Image.Threshold");
        Assert.Contains(threshold.Parameters, parameter => parameter.Name == "threshold" && parameter.DefaultValue == "128");
        Assert.Contains("IReadOnlyDictionary<string, string>? parameters", code);
        Assert.Contains("ParameterDouble(parameters", code);
    }

    [Fact]
    public void ExecutionManifest_RejectsOrdinaryOrCorruptedCSharp()
    {
        Assert.Throws<InvalidDataException>(() => CSharpExportManifest.Parse("public class Example {}"));
        Assert.Throws<InvalidDataException>(() => CSharpExportManifest.Parse(CSharpExportManifest.Marker + "not-base64"));
    }

    [Fact]
    public void CodeGenerator_ExportsOnlyTheSelectedResultBranch()
    {
        WorkflowNode Add(string type, string id) => new()
        {
            Id = id,
            Type = type,
            Name = type
        };
        var firstLoad = Add("Image.Load", "load_1");
        var firstGray = Add("Image.Gray", "gray_1");
        var secondLoad = Add("Image.Load", "load_2");
        var secondGray = Add("Image.Gray", "gray_2");
        var workflow = new WorkflowDocument
        {
            Nodes = { firstLoad, firstGray, secondLoad, secondGray },
            Connections =
            {
                new(firstLoad.Id, "Image", firstGray.Id, "Image"),
                new(secondLoad.Id, "Image", secondGray.Id, "Image")
            }
        };

        var code = new CSharpWorkflowGenerator(_registry).Generate(workflow, targetNodeId: secondGray.Id);

        Assert.Contains("(load_2)", code);
        Assert.Contains("(gray_2)", code);
        Assert.DoesNotContain("(load_1)", code);
        Assert.DoesNotContain("(gray_1)", code);
        Assert.Contains("return mat_gray_2.Clone();", code);
    }

    [Fact]
    public void CodeGenerator_RequiresAnExplicitTargetForMultipleResultBranches()
    {
        var first = LinearWorkflow(_registry);
        var second = new WorkflowDocument
        {
            Nodes =
            {
                new WorkflowNode { Id = "load_2", Type = "Image.Load" },
                new WorkflowNode { Id = "gray_2", Type = "Image.Gray" },
                new WorkflowNode { Id = "threshold_2", Type = "Image.Threshold" }
            },
            Connections =
            {
                new("load_2", "Image", "gray_2", "Image"),
                new("gray_2", "Image", "threshold_2", "Image")
            }
        };
        first.Nodes.AddRange(second.Nodes);
        first.Connections.AddRange(second.Connections);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new CSharpWorkflowGenerator(_registry).Generate(first));

        Assert.Contains("multiple image result nodes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static WorkflowDocument LinearWorkflow(OperatorRegistry registry)
    {
        WorkflowNode Add(string type)
        {
            registry.TryGetDescriptor(type, out var descriptor);
            return OpenCvOperatorCatalog.CreateNode(descriptor, 0, 0);
        }
        var load = Add("Image.Load");
        var gray = Add("Image.Gray");
        var threshold = Add("Image.Threshold");
        return new WorkflowDocument
        {
            Nodes = { load, gray, threshold },
            Connections = { new(load.Id, "Image", gray.Id, "Image"), new(gray.Id, "Image", threshold.Id, "Image") }
        };
    }

    private sealed class NoOp : IVisionOperator
    {
        public ValueTask ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
