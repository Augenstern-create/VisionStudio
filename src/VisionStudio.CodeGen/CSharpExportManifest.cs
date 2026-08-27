using System.Globalization;
using System.Text;
using System.Text.Json;
using VisionStudio.Core;

namespace VisionStudio.CodeGen;

public sealed record CSharpExportParameter(
    string Name,
    string DisplayName,
    JsonValueKind Kind,
    string DefaultValue,
    double? Minimum,
    double? Maximum,
    string? Description);

public sealed record CSharpExportNode(
    string Id,
    string Type,
    string DisplayName,
    IReadOnlyList<CSharpExportParameter> Parameters);

public sealed record CSharpExportConnection(string FromNode, string FromPort, string ToNode, string ToPort);

public sealed record CSharpExportManifest(
    int SchemaVersion,
    string Generator,
    string WorkflowName,
    string TargetNodeId,
    IReadOnlyList<CSharpExportNode> Nodes,
    IReadOnlyList<CSharpExportConnection> Connections) {
  public const int CurrentSchemaVersion = 1;
  public const string GeneratorId = "VisionStudio.CodeGen";
  public const string Marker = "// visionstudio-manifest-base64:";

  public static CSharpExportManifest Parse(string source) {
    string line = source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(item => item.StartsWith(Marker, StringComparison.Ordinal)) ?? throw new InvalidDataException("The C# file does not contain a Vision Studio execution manifest.");
    try {
      string json = Encoding.UTF8.GetString(Convert.FromBase64String(line[Marker.Length..].Trim()));
      var manifest = JsonSerializer.Deserialize<CSharpExportManifest>(json, WorkflowJson.Options)
          ?? throw new InvalidDataException("The C# execution manifest is empty.");
      if (manifest.SchemaVersion != CurrentSchemaVersion) {
        throw new NotSupportedException($"C# execution manifest schema {manifest.SchemaVersion} is not supported.");
      }

      if (!string.Equals(manifest.Generator, GeneratorId, StringComparison.Ordinal)) {
        throw new InvalidDataException("The C# execution manifest generator is not recognized.");
      }

      if (!manifest.Nodes.Any(node => node.Id == manifest.TargetNodeId)) {
        throw new InvalidDataException("The C# execution manifest target node does not exist.");
      }

      return manifest;
    } catch (FormatException exception) {
      throw new InvalidDataException("The C# execution manifest is not valid Base64.", exception);
    } catch (JsonException exception) {
      throw new InvalidDataException("The C# execution manifest is not valid JSON.", exception);
    }
  }

  internal static CSharpExportManifest Create(
      WorkflowDocument workflow,
      IReadOnlyList<WorkflowNode> topologicalOrder,
      string targetNodeId,
      OperatorRegistry registry) {
    var nodes = topologicalOrder.Select(node => {
      registry.TryGetDescriptor(node.Type, out var descriptor);
      var parameters = descriptor.Parameters.Select(parameter => new CSharpExportParameter(
          parameter.Name,
          parameter.DisplayName,
          parameter.Kind,
          Value(node, parameter),
          parameter.Minimum,
          parameter.Maximum,
          parameter.Description)).ToList();
      return new CSharpExportNode(node.Id, node.Type, node.Name ?? descriptor.DisplayName, parameters);
    }).ToList();
    return new CSharpExportManifest(
        CurrentSchemaVersion,
        GeneratorId,
        workflow.Name,
        targetNodeId,
        nodes,
        [.. workflow.Connections.Select(connection => new CSharpExportConnection(
            connection.FromNode, connection.FromPort, connection.ToNode, connection.ToPort))]);
  }

  internal string Encode() {
    string json = JsonSerializer.Serialize(this, WorkflowJson.Options);
    return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
  }

  private static string Value(WorkflowNode node, ParameterDefinition parameter) {
    if (node.Parameters.TryGetValue(parameter.Name, out var value)) {
      return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    return parameter.DefaultValue switch {
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      _ => parameter.DefaultValue?.ToString() ?? ""
    };
  }
}
