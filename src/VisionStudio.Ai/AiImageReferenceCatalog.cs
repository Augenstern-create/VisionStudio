using System.Text.RegularExpressions;
using VisionStudio.Core;

namespace VisionStudio.Ai;

public sealed record AiImageReference(string Reference, string NodeId, string Name, string RelativePath) {
  public string PromptToken => $"[{Reference}] {Name}";
}

public static partial class AiImageReferenceCatalog {
  public static IReadOnlyList<AiImageReference> Create(WorkflowDocument workflow) {
    return [.. workflow.Nodes
      .Where(node => node.Type == "Image.Load")
      .Select((node, index) => new AiImageReference($"IMG-{index + 1:00}", node.Id,
          string.IsNullOrWhiteSpace(node.Name) ? "加载图像" : node.Name!, node.GetParameter("path", "images/input/input.png")))];
  }

  public static AiImageReference? Resolve(WorkflowDocument workflow, string text, string? selectedNodeId = null) {
    var references = Create(workflow);
    // An explicit reference in the prompt is the user's latest intent and must
    // take precedence over a stale canvas selection.
    foreach (var item in references) {
      if (text.Contains(item.Reference, StringComparison.OrdinalIgnoreCase) ||
          text.Contains($"#{int.Parse(item.Reference[4..])}", StringComparison.OrdinalIgnoreCase)) {
        return item;
      }
    }
    var numeric = ImageNumberRegex().Match(text);
    if (numeric.Success && int.TryParse(numeric.Groups[1].Value, out int number) && number > 0 && number <= references.Count) {
      return references[number - 1];
    }

    // A display name is only a safe reference when it identifies exactly one
    // image node. Generic duplicate names such as "加载图像" must never steal
    // an explicit IMG reference from a later node.
    var nameMatches = references
        .Where(item => !string.IsNullOrWhiteSpace(item.Name) && text.Contains(item.Name, StringComparison.CurrentCultureIgnoreCase))
        .ToList();
    if (nameMatches.Count == 1) {
      return nameMatches[0];
    }

    if (!string.IsNullOrWhiteSpace(selectedNodeId)) {
      var selected = references.FirstOrDefault(item => item.NodeId == selectedNodeId);
      if (selected is not null) {
        return selected;
      }
    }
    return references.Count == 1 ? references[0] : null;
  }

  [GeneratedRegex(@"(?:图片|图像|模块|节点)\s*(?:#|编号)?\s*(\d+)", RegexOptions.IgnoreCase)]
  private static partial Regex ImageNumberRegex();
}
