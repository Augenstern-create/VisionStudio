using System.Collections;
using System.Globalization;
using Emgu.CV;
using VisionStudio.Core;

namespace VisionStudio.Operators.OpenCv;

public sealed class OpenCvWorkflowAutomationRunner(OperatorRegistry registry) : IWorkflowAutomationRunner {
  public async Task<WorkflowAutomationRunResult> RunAsync(
      string requestId,
      ProjectHandle project,
      WorkflowDocument workflow,
      string targetNodeId,
      CancellationToken cancellationToken = default) {
    using var result = await new WorkflowExecutor(registry, project.RootPath, new OpenCvProjectCache(project.RootPath))
        .ExecuteAsync(workflow, targetNodeId, cancellationToken);
    var node = workflow.Nodes.Single(item => item.Id == targetNodeId);
    registry.TryGetDescriptor(node.Type, out var descriptor);
    var values = result.GetNodeOutputs(targetNodeId);
    var outputs = new List<WorkflowAutomationOutput>();
    foreach (var port in descriptor.Outputs) {
      cancellationToken.ThrowIfCancellationRequested();
      if (!values.TryGetValue(port.Name, out object? value)) {
        continue;
      }

      if (value is Mat image) {
        string relative = $"images/cache/assistant/{Safe(requestId)}-{Safe(targetNodeId)}-{Safe(port.Name)}.png";
        string path = ProjectPaths.ResolveInside(project.RootPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp.png");
        try {
          OpenCvFileCodec.Write(temporary, image);
          File.Move(temporary, path, overwrite: true);
        } finally {
          if (File.Exists(temporary)) {
            File.Delete(temporary);
          }
        }
        outputs.Add(new(port.Name, port.Type, $"{image.Width}x{image.Height}, {image.NumberOfChannels} channel(s)", relative));
      } else {
        outputs.Add(new(port.Name, port.Type, Summarize(value)));
      }
    }
    return new(requestId, targetNodeId, true, outputs, result.Timings);
  }

  private static string Summarize(object value) {
    return value switch {
      string text => text,
      ICollection collection => $"Collection({collection.Count})",
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      _ => value.ToString() ?? value.GetType().Name
    };
  }

  private static string Safe(string value) {
    return string.Concat(value.Select(character =>
      char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
  }
}
