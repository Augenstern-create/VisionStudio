using System.Globalization;
using System.Text;
using System.Text.Json;
using VisionStudio.Core;

namespace VisionStudio.CodeGen;

public sealed class CSharpWorkflowGenerator(OperatorRegistry registry)
{
    private static readonly HashSet<string> StandaloneOperators = ["Image.Load", "Image.Gray", "Image.GaussianBlur", "Image.Threshold", "Image.AddWeighted", "Image.Save"];

    public string Generate(WorkflowDocument workflow, string className = "GeneratedVisionAlgorithm")
    {
        var validation = WorkflowValidator.Validate(workflow, registry);
        if (!validation.IsValid) throw new WorkflowValidationException(validation.Errors);
        className = Identifier(className);
        if (validation.TopologicalOrder.Any(node => !StandaloneOperators.Contains(node.Type)))
            return GenerateRuntimeBacked(workflow, validation, className);
        var variables = new Dictionary<(string Node, string Port), string>();
        var builder = new StringBuilder();
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Collections.Generic;");
        builder.AppendLine("using System.Drawing;");
        builder.AppendLine("using System.IO;");
        builder.AppendLine("using Emgu.CV;");
        builder.AppendLine("using Emgu.CV.CvEnum;");
        builder.AppendLine();
        builder.AppendLine($"public sealed class {className}");
        builder.AppendLine("{");
        builder.AppendLine("    public Mat Process(string projectRoot)");
        builder.AppendLine("    {");

        foreach (var node in validation.TopologicalOrder)
        {
            var input = workflow.Connections.Where(c => c.ToNode == node.Id)
                .ToDictionary(c => c.ToPort, c => variables[(c.FromNode, c.FromPort)], StringComparer.Ordinal);
            var output = "mat_" + Identifier(node.Id);
            EmitNode(builder, node, input, output);
            variables[(node.Id, "Image")] = output;
        }

        var last = validation.TopologicalOrder.LastOrDefault()
            ?? throw new InvalidOperationException("Cannot generate an empty workflow.");
        builder.AppendLine($"        return {variables[(last.Id, "Image")]}.Clone();");
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
        builder.AppendLine("}");
        return builder.ToString();
    }

    private string GenerateRuntimeBacked(WorkflowDocument workflow, WorkflowValidationResult validation, string className)
    {
        var last = validation.TopologicalOrder.LastOrDefault() ?? throw new InvalidOperationException("Cannot generate an empty workflow.");
        registry.TryGetDescriptor(last.Type, out var descriptor);
        var imagePort = descriptor.Outputs.FirstOrDefault(port => port.Type == NodeValueType.Mat)
            ?? throw new NotSupportedException("The final node has no image output. Runtime-backed C# export currently returns Mat.");
        var json = WorkflowJson.Serialize(workflow);
        var builder = new StringBuilder();
        builder.AppendLine("using Emgu.CV;");
        builder.AppendLine("using VisionStudio.Core;");
        builder.AppendLine("using VisionStudio.Operators.OpenCv;");
        builder.AppendLine();
        builder.AppendLine($"public sealed class {className}");
        builder.AppendLine("{");
        builder.AppendLine("    public Mat Process(string projectRoot)");
        builder.AppendLine("    {");
        builder.AppendLine($"        var workflow = WorkflowJson.Deserialize({Literal(json)});");
        builder.AppendLine("        var registry = OpenCvOperatorCatalog.CreateRegistry();");
        builder.AppendLine($"        using var result = new WorkflowExecutor(registry, projectRoot, new OpenCvProjectCache(projectRoot)).ExecuteAsync(workflow, {Literal(last.Id)}).GetAwaiter().GetResult();");
        builder.AppendLine($"        return result.GetOutput<Mat>({Literal(last.Id)}, {Literal(imagePort.Name)}).Clone();");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void EmitNode(StringBuilder builder, WorkflowNode node, IReadOnlyDictionary<string, string> inputs, string output)
    {
        builder.AppendLine($"        // {node.Name ?? node.Type} ({node.Id})");
        switch (node.Type)
        {
            case "Image.Load":
                builder.AppendLine($"        using var {output} = new Mat();");
                builder.AppendLine($"        CvInvoke.Imdecode(File.ReadAllBytes(ProjectPath(projectRoot, {Literal(node.GetParameter("path", "images/input/input.png"))})), ImreadModes.Unchanged, {output});");
                builder.AppendLine($"        if ({output}.IsEmpty) throw new InvalidDataException(\"Could not decode input image.\");");
                break;
            case "Image.Gray":
                builder.AppendLine($"        using var {output} = new Mat();");
                builder.AppendLine($"        if ({inputs["Image"]}.NumberOfChannels == 1) {inputs["Image"]}.CopyTo({output});");
                builder.AppendLine($"        else CvInvoke.CvtColor({inputs["Image"]}, {output}, {inputs["Image"]}.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);");
                break;
            case "Image.GaussianBlur":
                builder.AppendLine($"        using var {output} = new Mat();");
                var kernel = node.GetParameter("kernelSize", 5);
                builder.AppendLine($"        CvInvoke.GaussianBlur({inputs["Image"]}, {output}, new Size({kernel}, {kernel}), {Number(node.GetParameter("sigma", 1.5d))});");
                break;
            case "Image.Threshold":
                builder.AppendLine($"        using var {output} = new Mat();");
                builder.AppendLine($"        CvInvoke.Threshold({inputs["Image"]}, {output}, {Number(node.GetParameter("threshold", 128d))}, {Number(node.GetParameter("maxValue", 255d))}, ThresholdType.{Identifier(node.GetParameter("mode", "Binary"))});");
                break;
            case "Image.AddWeighted":
                builder.AppendLine($"        using var {output} = new Mat();");
                builder.AppendLine($"        CvInvoke.AddWeighted({inputs["ImageA"]}, {Number(node.GetParameter("alpha", .5d))}, {inputs["ImageB"]}, {Number(node.GetParameter("beta", .5d))}, {Number(node.GetParameter("gamma", 0d))}, {output});");
                break;
            case "Image.Save":
                builder.AppendLine($"        var path_{Identifier(node.Id)} = ProjectPath(projectRoot, {Literal(node.GetParameter("path", "images/output/result.png"))});");
                builder.AppendLine($"        Directory.CreateDirectory(Path.GetDirectoryName(path_{Identifier(node.Id)})!);");
                builder.AppendLine($"        File.WriteAllBytes(path_{Identifier(node.Id)}, CvInvoke.Imencode(Path.GetExtension(path_{Identifier(node.Id)}), {inputs["Image"]}, Array.Empty<KeyValuePair<ImwriteFlags, int>>()));");
                builder.AppendLine($"        using var {output} = {inputs["Image"]}.Clone();");
                break;
            default:
                throw new NotSupportedException($"Code generation is not implemented for '{node.Type}'.");
        }
        builder.AppendLine();
    }

    private static string Identifier(string value)
    {
        var result = new string(value.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        if (string.IsNullOrEmpty(result)) result = "Generated";
        if (!char.IsLetter(result[0]) && result[0] != '_') result = "_" + result;
        return result;
    }

    private static string Literal(string value) => JsonSerializer.Serialize(value);
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
