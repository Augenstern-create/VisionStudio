using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Core;

[JsonConverter(typeof(JsonStringEnumConverter<NodeValueType>))]
public enum NodeValueType
{
    Any,
    Mat,
    Integer,
    Double,
    Boolean,
    String,
    Point,
    Line,
    Circle,
    Rectangle,
    Contours,
    Points,
    Matrix,
    Collection
}

public sealed record PortDefinition(
    string Name,
    NodeValueType Type,
    bool Required = true,
    string? DisplayName = null,
    string? Description = null);

public sealed record ParameterDefinition(
    string Name,
    string DisplayName,
    JsonValueKind Kind,
    object DefaultValue,
    double? Minimum = null,
    double? Maximum = null,
    string? Description = null);

public sealed record OperatorDescriptor(
    string Type,
    string DisplayName,
    string Category,
    IReadOnlyList<PortDefinition> Inputs,
    IReadOnlyList<PortDefinition> Outputs,
    IReadOnlyList<ParameterDefinition> Parameters,
    string? Description = null,
    string? EnglishName = null,
    string? EnglishDescription = null);

public sealed class WorkflowNode
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string? Name { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public Dictionary<string, JsonElement> Parameters { get; init; } = new(StringComparer.Ordinal);

    public T GetParameter<T>(string name, T fallback)
    {
        if (!Parameters.TryGetValue(name, out var value)) return fallback;
        return value.Deserialize<T>(WorkflowJson.Options) ?? fallback;
    }

    public void SetParameter<T>(string name, T value) =>
        Parameters[name] = JsonSerializer.SerializeToElement(value, WorkflowJson.Options);
}

public sealed record WorkflowConnection(
    string FromNode,
    string FromPort,
    string ToNode,
    string ToPort);

public sealed class WorkflowDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Name { get; set; } = "Main";
    public List<WorkflowNode> Nodes { get; init; } = [];
    public List<WorkflowConnection> Connections { get; init; } = [];
}

public static class WorkflowJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(WorkflowDocument workflow) =>
        JsonSerializer.Serialize(workflow, Options);

    public static WorkflowDocument Deserialize(string json)
    {
        var workflow = JsonSerializer.Deserialize<WorkflowDocument>(json, Options)
            ?? throw new JsonException("Workflow content is empty.");
        if (workflow.SchemaVersion > WorkflowDocument.CurrentSchemaVersion)
            throw new NotSupportedException($"Workflow schema {workflow.SchemaVersion} is newer than supported schema {WorkflowDocument.CurrentSchemaVersion}.");
        return workflow;
    }
}
