using System.Globalization;
using System.Text;
using System.Text.Json;
using VisionStudio.Core;

namespace VisionStudio.CodeGen;

public sealed class CSharpWorkflowGenerator(OperatorRegistry registry) {
  private static readonly HashSet<string> StandaloneOperators =
  [
      "Image.Load", "Image.Gray", "Image.GaussianBlur", "Image.Threshold", "Image.AddWeighted", "Image.Save",
        "Threshold.Otsu", "Threshold.Triangle", "Morphology.Erode", "Morphology.Dilate", "Morphology.Open",
        "Morphology.Close", "Morphology.Gradient", "Morphology.TopHat", "Morphology.BlackHat", "Blob.Contours",
        "Contour.PathMetrics"
  ];

  public string Generate(
      WorkflowDocument workflow,
      string className = "GeneratedVisionAlgorithm",
      string? targetNodeId = null) {
    var target = ResolveTarget(workflow, targetNodeId);
    var exportedWorkflow = CreateUpstreamSlice(workflow, target.Id);
    var validation = WorkflowValidator.Validate(exportedWorkflow, registry);
    if (!validation.IsValid) {
      throw new WorkflowValidationException(validation.Errors);
    }

    className = Identifier(className);
    var unsupported = validation.TopologicalOrder
        .Where(node => !StandaloneOperators.Contains(node.Type))
        .Select(node => $"{node.Name ?? node.Type} [{node.Type}]")
        .ToList();
    if (unsupported.Count > 0) {
      throw new NotSupportedException(
          "Standalone C# export is not implemented for: " + string.Join(", ", unsupported) +
          ". No source file was generated because exported code must be independently executable.");
    }

    var variables = new Dictionary<(string Node, string Port), string>();
    var manifest = CSharpExportManifest.Create(exportedWorkflow, validation.TopologicalOrder, target.Id, registry);
    var builder = new StringBuilder();
    builder.AppendLine(CSharpExportManifest.Marker + manifest.Encode());
    builder.AppendLine("using System;");
    builder.AppendLine("using System.Collections.Generic;");
    builder.AppendLine("using System.Drawing;");
    builder.AppendLine("using System.Globalization;");
    builder.AppendLine("using System.IO;");
    builder.AppendLine("using System.Linq;");
    builder.AppendLine("using Emgu.CV;");
    builder.AppendLine("using Emgu.CV.CvEnum;");
    builder.AppendLine("using Emgu.CV.Structure;");
    builder.AppendLine("using Emgu.CV.Util;");
    builder.AppendLine();
    builder.AppendLine($"public sealed class {className}");
    builder.AppendLine("{");
    builder.AppendLine("    public Mat Process(string projectRoot, IReadOnlyDictionary<string, string>? parameters = null)");
    builder.AppendLine("    {");

    foreach (var node in validation.TopologicalOrder) {
      var input = exportedWorkflow.Connections.Where(c => c.ToNode == node.Id)
          .ToDictionary(c => c.ToPort, c => variables[(c.FromNode, c.FromPort)], StringComparer.Ordinal);
      string output = "mat_" + Identifier(node.Id);
      EmitNode(builder, node, input, output);
      registry.TryGetDescriptor(node.Type, out var descriptor);
      foreach (var port in descriptor.Outputs) {
        variables[(node.Id, port.Name)] = port.Name == "Image" ? output : ValueVariable(node.Id, port.Name);
      }
    }

    builder.AppendLine($"        return {variables[(target.Id, "Image")]}.Clone();");
    builder.AppendLine("    }");
    builder.AppendLine();
    builder.AppendLine("    private static string ProjectPath(string root, string relative)");
    builder.AppendLine("    {");
    builder.AppendLine("        if (Path.IsPathRooted(relative)) throw new InvalidOperationException(\"Expected a project-relative path.\");");
    builder.AppendLine("        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;");
    builder.AppendLine("        var path = Path.GetFullPath(Path.Combine(normalizedRoot, relative));");
    builder.AppendLine("        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(\"Path escapes project root.\");");
    builder.AppendLine("        return path;");
    builder.AppendLine("    }");
    builder.AppendLine();
    builder.AppendLine("    private static string Parameter(IReadOnlyDictionary<string, string>? values, string node, string name, string fallback) =>");
    builder.AppendLine("        values is not null && values.TryGetValue(node + \".\" + name, out var value) ? value : fallback;");
    builder.AppendLine();
    builder.AppendLine("    private static int ParameterInt(IReadOnlyDictionary<string, string>? values, string node, string name, int fallback) =>");
    builder.AppendLine("        int.TryParse(Parameter(values, node, name, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($\"Invalid integer parameter: {node}.{name}\");");
    builder.AppendLine();
    builder.AppendLine("    private static double ParameterDouble(IReadOnlyDictionary<string, string>? values, string node, string name, double fallback) =>");
    builder.AppendLine("        double.TryParse(Parameter(values, node, name, fallback.ToString(\"R\", CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($\"Invalid numeric parameter: {node}.{name}\");");
    builder.AppendLine();
    builder.AppendLine("    private static bool ParameterBool(IReadOnlyDictionary<string, string>? values, string node, string name, bool fallback) =>");
    builder.AppendLine("        bool.TryParse(Parameter(values, node, name, fallback.ToString(CultureInfo.InvariantCulture)), out var value) ? value : throw new ArgumentException($\"Invalid Boolean parameter: {node}.{name}\");");
    builder.AppendLine();
    builder.AppendLine("    private static Mat Gray(Mat source)");
    builder.AppendLine("    {");
    builder.AppendLine("        var output = new Mat();");
    builder.AppendLine("        if (source.NumberOfChannels == 1) source.CopyTo(output);");
    builder.AppendLine("        else CvInvoke.CvtColor(source, output, source.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);");
    builder.AppendLine("        return output;");
    builder.AppendLine("    }");
    builder.AppendLine();
    builder.AppendLine("    private static Mat Color(Mat source)");
    builder.AppendLine("    {");
    builder.AppendLine("        var output = new Mat();");
    builder.AppendLine("        if (source.NumberOfChannels == 1) CvInvoke.CvtColor(source, output, ColorConversion.Gray2Bgr);");
    builder.AppendLine("        else if (source.NumberOfChannels == 4) CvInvoke.CvtColor(source, output, ColorConversion.Bgra2Bgr);");
    builder.AppendLine("        else source.CopyTo(output);");
    builder.AppendLine("        return output;");
    builder.AppendLine("    }");
    builder.AppendLine();
    builder.AppendLine("    private static void Put(Mat image, string text) =>");
    builder.AppendLine("        CvInvoke.PutText(image, text, new Point(12, 28), FontFace.HersheySimplex, .65, new MCvScalar(0, 255, 255), 2);");
    builder.AppendLine("}");
    return builder.ToString();
  }

  private WorkflowNode ResolveTarget(WorkflowDocument workflow, string? targetNodeId) {
    if (workflow.Nodes.Count == 0) {
      throw new InvalidOperationException("Cannot generate an empty workflow.");
    }

    if (!string.IsNullOrWhiteSpace(targetNodeId)) {
      var selected = workflow.Nodes.SingleOrDefault(node => string.Equals(node.Id, targetNodeId, StringComparison.Ordinal))
          ?? throw new InvalidOperationException($"Export target node '{targetNodeId}' does not exist.");
      EnsureImageOutput(selected);
      return selected;
    }

    var nodesWithOutgoingConnections = workflow.Connections
        .Select(connection => connection.FromNode)
        .ToHashSet(StringComparer.Ordinal);
    var imageSinks = workflow.Nodes
        .Where(node => !nodesWithOutgoingConnections.Contains(node.Id) && HasImageOutput(node))
        .ToList();
    return imageSinks.Count switch {
      1 => imageSinks[0],
      0 => throw new InvalidOperationException("The workflow has no terminal node with an image output."),
      _ => throw new InvalidOperationException(
          "The workflow has multiple image result nodes. Select the result node before exporting, or pass its node id to the CLI export command.")
    };
  }

  private bool HasImageOutput(WorkflowNode node) {
    return registry.TryGetDescriptor(node.Type, out var descriptor) &&
      descriptor.Outputs.Any(output => output.Type == NodeValueType.Mat);
  }

  private void EnsureImageOutput(WorkflowNode node) {
    if (!HasImageOutput(node)) {
      throw new NotSupportedException($"The export target '{node.Id}' has no image output. C# export currently returns Mat.");
    }
  }

  private static WorkflowDocument CreateUpstreamSlice(WorkflowDocument workflow, string targetNodeId) {
    var included = new HashSet<string>(StringComparer.Ordinal) { targetNodeId };
    var pending = new Stack<string>();
    pending.Push(targetNodeId);
    while (pending.TryPop(out string? nodeId)) {
      foreach (var connection in workflow.Connections.Where(connection => connection.ToNode == nodeId)) {
        if (included.Add(connection.FromNode)) {
          pending.Push(connection.FromNode);
        }
      }
    }

    return new WorkflowDocument {
      SchemaVersion = workflow.SchemaVersion,
      Name = workflow.Name,
      Nodes = [.. workflow.Nodes.Where(node => included.Contains(node.Id))],
      Connections = [.. workflow.Connections.Where(connection => included.Contains(connection.FromNode) && included.Contains(connection.ToNode))]
    };
  }

  private static void EmitNode(StringBuilder builder, WorkflowNode node, IReadOnlyDictionary<string, string> inputs, string output) {
    builder.AppendLine($"        // {node.Name ?? node.Type} ({node.Id})");
    switch (node.Type) {
      case "Image.Load":
        builder.AppendLine($"        using var {output} = new Mat();");
        builder.AppendLine($"        CvInvoke.Imdecode(File.ReadAllBytes(ProjectPath(projectRoot, Parameter(parameters, {Literal(node.Id)}, \"path\", {Literal(node.GetParameter("path", "images/input/input.png"))}))), ImreadModes.Unchanged, {output});");
        builder.AppendLine($"        if ({output}.IsEmpty) throw new InvalidDataException(\"Could not decode input image.\");");
        break;
      case "Image.Gray":
        builder.AppendLine($"        using var {output} = new Mat();");
        builder.AppendLine($"        if ({inputs["Image"]}.NumberOfChannels == 1) {inputs["Image"]}.CopyTo({output});");
        builder.AppendLine($"        else CvInvoke.CvtColor({inputs["Image"]}, {output}, {inputs["Image"]}.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);");
        break;
      case "Image.GaussianBlur":
        builder.AppendLine($"        using var {output} = new Mat();");
        int kernel = node.GetParameter("kernelSize", 5);
        builder.AppendLine($"        var kernel_{Identifier(node.Id)} = ParameterInt(parameters, {Literal(node.Id)}, \"kernelSize\", {kernel});");
        builder.AppendLine($"        CvInvoke.GaussianBlur({inputs["Image"]}, {output}, new Size(kernel_{Identifier(node.Id)}, kernel_{Identifier(node.Id)}), ParameterDouble(parameters, {Literal(node.Id)}, \"sigma\", {Number(node.GetParameter("sigma", 1.5d))}));");
        break;
      case "Image.Threshold":
        builder.AppendLine($"        using var {output} = new Mat();");
        builder.AppendLine($"        CvInvoke.Threshold({inputs["Image"]}, {output}, ParameterDouble(parameters, {Literal(node.Id)}, \"threshold\", {Number(node.GetParameter("threshold", 128d))}), ParameterDouble(parameters, {Literal(node.Id)}, \"maxValue\", {Number(node.GetParameter("maxValue", 255d))}), Enum.Parse<ThresholdType>(Parameter(parameters, {Literal(node.Id)}, \"mode\", {Literal(node.GetParameter("mode", "Binary"))}), true));");
        break;
      case "Image.AddWeighted":
        builder.AppendLine($"        using var {output} = new Mat();");
        builder.AppendLine($"        CvInvoke.AddWeighted({inputs["ImageA"]}, ParameterDouble(parameters, {Literal(node.Id)}, \"alpha\", {Number(node.GetParameter("alpha", .5d))}), {inputs["ImageB"]}, ParameterDouble(parameters, {Literal(node.Id)}, \"beta\", {Number(node.GetParameter("beta", .5d))}), ParameterDouble(parameters, {Literal(node.Id)}, \"gamma\", {Number(node.GetParameter("gamma", 0d))}), {output});");
        break;
      case "Image.Save":
        builder.AppendLine($"        var path_{Identifier(node.Id)} = ProjectPath(projectRoot, Parameter(parameters, {Literal(node.Id)}, \"path\", {Literal(node.GetParameter("path", "images/output/result.png"))}));");
        builder.AppendLine($"        Directory.CreateDirectory(Path.GetDirectoryName(path_{Identifier(node.Id)})!);");
        builder.AppendLine($"        File.WriteAllBytes(path_{Identifier(node.Id)}, CvInvoke.Imencode(Path.GetExtension(path_{Identifier(node.Id)}), {inputs["Image"]}, Array.Empty<KeyValuePair<ImwriteFlags, int>>()));");
        builder.AppendLine($"        using var {output} = {inputs["Image"]}.Clone();");
        break;
      case "Threshold.Otsu":
      case "Threshold.Triangle":
        string thresholdMode = node.Type == "Threshold.Otsu" ? "Otsu" : "Triangle";
        builder.AppendLine($"        using var gray_{Identifier(node.Id)} = Gray({inputs["Image"]});");
        builder.AppendLine($"        using var {output} = new Mat();");
        builder.AppendLine($"        var {ValueVariable(node.Id, "Threshold")} = CvInvoke.Threshold(gray_{Identifier(node.Id)}, {output}, 0, 255, ThresholdType.Binary | ThresholdType.{thresholdMode});");
        break;
      case "Morphology.Erode":
      case "Morphology.Dilate":
      case "Morphology.Open":
      case "Morphology.Close":
      case "Morphology.Gradient":
      case "Morphology.TopHat":
      case "Morphology.BlackHat":
        string operation = node.Type[(node.Type.IndexOf('.') + 1)..] switch {
          "Erode" => "Erode",
          "Dilate" => "Dilate",
          "Open" => "Open",
          "Close" => "Close",
          "Gradient" => "Gradient",
          "TopHat" => "Tophat",
          _ => "Blackhat"
        };
        int morphKernel = Math.Max(1, node.GetParameter("kernelSize", 3));
        builder.AppendLine($"        var morphKernel_{Identifier(node.Id)} = Math.Max(1, ParameterInt(parameters, {Literal(node.Id)}, \"kernelSize\", {morphKernel}));");
        builder.AppendLine($"        using var element_{Identifier(node.Id)} = CvInvoke.GetStructuringElement(MorphShapes.Rectangle, new Size(morphKernel_{Identifier(node.Id)}, morphKernel_{Identifier(node.Id)}), new Point(-1, -1));");
        builder.AppendLine($"        using var {output} = new Mat();");
        builder.AppendLine($"        CvInvoke.MorphologyEx({inputs["Image"]}, {output}, MorphOp.{operation}, element_{Identifier(node.Id)}, new Point(-1, -1), ParameterInt(parameters, {Literal(node.Id)}, \"iterations\", {node.GetParameter("iterations", 1)}), BorderType.Reflect101, default);");
        break;
      case "Blob.Contours":
        string contourId = Identifier(node.Id);
        builder.AppendLine($"        using var gray_{contourId} = Gray({inputs["Image"]});");
        builder.AppendLine($"        using var work_{contourId} = gray_{contourId}.Clone();");
        builder.AppendLine($"        using var vectors_{contourId} = new VectorOfVectorOfPoint();");
        builder.AppendLine($"        using var hierarchy_{contourId} = new Mat();");
        builder.AppendLine($"        CvInvoke.FindContours(work_{contourId}, vectors_{contourId}, hierarchy_{contourId}, RetrType.External, ChainApproxMethod.ChainApproxSimple);");
        builder.AppendLine($"        var {ValueVariable(node.Id, "Contours")} = Enumerable.Range(0, vectors_{contourId}.Size).Select(i => vectors_{contourId}[i].ToArray()).ToList();");
        builder.AppendLine($"        var {ValueVariable(node.Id, "Count")} = {ValueVariable(node.Id, "Contours")}.Count;");
        builder.AppendLine($"        using var {output} = Color({inputs["Image"]});");
        builder.AppendLine($"        for (var i = 0; i < vectors_{contourId}.Size; i++) CvInvoke.DrawContours({output}, vectors_{contourId}, i, new MCvScalar(0, 255, 0), 2);");
        builder.AppendLine($"        Put({output}, $\"Contours: {{{ValueVariable(node.Id, "Count")}}}\");");
        break;
      case "Contour.PathMetrics":
        EmitPathMetrics(builder, node, inputs, output);
        break;
      default:
        throw new NotSupportedException($"Code generation is not implemented for '{node.Type}'.");
    }
    builder.AppendLine();
  }

  private static void EmitPathMetrics(StringBuilder builder, WorkflowNode node, IReadOnlyDictionary<string, string> inputs, string output) {
    string id = Identifier(node.Id);
    string selected = $"selected_{id}";
    string contours = inputs["Contours"];
    builder.AppendLine($"        var {selected} = new List<(Point[] Points, double Perimeter, double Length)>();");
    builder.AppendLine($"        foreach (var points in {contours})");
    builder.AppendLine("        {");
    builder.AppendLine("            using var vector = new VectorOfPoint(points);");
    builder.AppendLine("            var area = Math.Abs(CvInvoke.ContourArea(vector));");
    builder.AppendLine("            var perimeter = CvInvoke.ArcLength(vector, true);");
    builder.AppendLine("            var box = CvInvoke.MinAreaRect(vector).Size;");
    builder.AppendLine("            var major = Math.Max(box.Width, box.Height);");
    builder.AppendLine("            var minor = Math.Max(1d, Math.Min(box.Width, box.Height));");
    builder.AppendLine("            var elongation = major / minor;");
    builder.AppendLine("            var length = Math.Max(major, perimeter / 2d - minor);");
    builder.AppendLine($"            if (area >= ParameterDouble(parameters, {Literal(node.Id)}, \"minArea\", {Number(node.GetParameter("minArea", 30d))}) && length >= ParameterDouble(parameters, {Literal(node.Id)}, \"minLength\", {Number(node.GetParameter("minLength", 20d))}) && elongation >= ParameterDouble(parameters, {Literal(node.Id)}, \"minElongation\", {Number(node.GetParameter("minElongation", 1.5d))}))");
    builder.AppendLine($"                {selected}.Add((points, perimeter, length));");
    builder.AppendLine("        }");
    builder.AppendLine($"        {selected} = {selected}.OrderByDescending(item => item.Length).Take(ParameterInt(parameters, {Literal(node.Id)}, \"maxCount\", {node.GetParameter("maxCount", 100)})).ToList();");
    builder.AppendLine($"        using var {output} = Color({inputs["Image"]});");
    builder.AppendLine($"        using var selectedVectors_{id} = new VectorOfVectorOfPoint();");
    builder.AppendLine($"        foreach (var item in {selected}) selectedVectors_{id}.Push(new VectorOfPoint(item.Points));");
    builder.AppendLine($"        for (var i = 0; i < selectedVectors_{id}.Size; i++) CvInvoke.DrawContours({output}, selectedVectors_{id}, i, new MCvScalar(0, 0, 255), 2);");
    builder.AppendLine($"        var {ValueVariable(node.Id, "Count")} = {selected}.Count;");
    builder.AppendLine($"        var {ValueVariable(node.Id, "PixelLength")} = {selected}.Sum(item => item.Length);");
    builder.AppendLine($"        var {ValueVariable(node.Id, "TotalPerimeter")} = {selected}.Sum(item => item.Perimeter);");
    builder.AppendLine($"        var {ValueVariable(node.Id, "Summary")} = $\"Elongated targets: {{{ValueVariable(node.Id, "Count")}}}; estimated path length: {{{ValueVariable(node.Id, "PixelLength")}:F1}}px; total perimeter: {{{ValueVariable(node.Id, "TotalPerimeter")}:F1}}px.\";");
    builder.AppendLine($"        Put({output}, $\"Count: {{{ValueVariable(node.Id, "Count")}}}  Length: {{{ValueVariable(node.Id, "PixelLength")}:F1}}px\");");
  }

  private static string ValueVariable(string nodeId, string port) {
    return "value_" + Identifier(nodeId) + "_" + Identifier(port);
  }

  private static string Identifier(string value) {
    string result = new string([.. value.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);
    if (string.IsNullOrEmpty(result)) {
      result = "Generated";
    }

    if (!char.IsLetter(result[0]) && result[0] != '_') {
      result = "_" + result;
    }

    return result;
  }

  private static string Literal(string value) {
    return JsonSerializer.Serialize(value);
  }

  private static string Number(double value) {
    return value.ToString("R", CultureInfo.InvariantCulture);
  }
}
