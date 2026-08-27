using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Ai.Abstractions;
using VisionStudio.Core;

namespace VisionStudio.Ai;

public sealed class AiWorkflowAgent(IWorkflowAutomationApi workflow, IAiProvider provider) {
  private static readonly JsonSerializerOptions PromptJsonOptions = new(WorkflowJson.Options) {
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  private const string SystemPrompt = """
You are the planning component of Vision Studio. Return one strict JSON object only.
Never emit C#, shell commands, file operations, or a complete workflow file.
Allowed action types: add_pipeline, add_node, remove_node, connect_nodes, set_parameter.
Use only operator ids and ports present in the supplied catalog. Preserve the selected input node.
The operators array is the complete runtime catalog currently available in the application, not a search result subset. Each compact index entry uses id=name. recommendedOperators contain the exact ports and parameters for operators relevant to this request. You may use any listed operator, but prefer recommendedOperators because their connection contract is supplied. Never invent an operator that is absent from operators.
Schema: {"summary":"...","targetNodeId":"...","actions":[{"type":"add_node","nodeId":"...","operatorId":"..."},{"type":"connect_nodes","fromNode":"...","fromPort":"Image","toNode":"...","toPort":"Image"}]}
For a new sequential pipeline, strongly prefer one compact action: {"type":"add_pipeline","fromNode":"EXISTING_SOURCE_NODE_ID","operatorIds":["Image.Gray","Image.GaussianBlur"],"nodeIds":["gray","blur"]}. The host expands it and connects compatible ports. operatorIds and nodeIds must have equal lengths. targetNodeId is the last nodeIds value.
When the supplied context contains compactPlanTemplate, copy that template as the single action. Do not expand it into add_node or connect_nodes actions and do not omit any operator.
Build an inspectable multi-stage vision pipeline by default: preprocessing, segmentation, cleanup, feature extraction, then measurement/result display.
Do not choose a one-click domain-specific operator merely because its name matches the request. Use such an operator only when the user explicitly names it or explicitly asks for a specialized one-click solution.
For dark thin defects such as cracks, prefer grayscale -> smoothing -> BlackHat -> Otsu -> morphology -> contours -> PathMetrics when those operators are supplied.
Reuse compatible existing nodes when practical. Every add_node.nodeId must be a short unique instance alias such as ai_gray_1; operatorId is the catalog interface such as Image.Gray. Never use an operatorId as fromNode, toNode, targetNodeId, or set_parameter.nodeId.
Connections may reference either existing graph node ids or aliases created by add_node in this plan.
The target node must expose the requested structured measurements and preview. Keep plans focused and never emit more than 24 actions.
Every required input on every newly added node must have exactly one connection. An input accepts at most one edge; an output may fan out to multiple downstream inputs. Do not create redundant parallel edges or connect an output merely because it exists.
""";

  public async Task<AiAssistantResponse> ProcessAsync(AiAssistantRequest request, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
    var snapshot = await workflow.GetSnapshotAsync(cancellationToken);
    var catalog = await workflow.GetOperatorCatalogAsync(cancellationToken);
    var matches = PlanningOperators(catalog, request.Text);
    bool isCrackComposition = IsCrackCompositionRequest(request.Text);
    string[] crackNodeIds = isCrackComposition
        ? CreateUniqueNodeIds(snapshot.Workflow, ["gray", "blur", "blackhat", "otsu", "close", "contours", "metrics"])
        : [];
    string context = JsonSerializer.Serialize(new {
      requirement = request.Text,
      selectedNodeId = request.SelectedNodeId,
      graph = new { snapshot.Revision, nodes = snapshot.Workflow.Nodes.Select(node => new { node.Id, node.Type, node.Name }), connections = snapshot.Workflow.Connections },
      operatorCount = catalog.Count,
      operators = catalog.Select(descriptor => $"{descriptor.Type}={descriptor.DisplayName}"),
      recommendedOperators = matches.Select(match => new {
        id = match.Descriptor.Type,
        name = match.Descriptor.DisplayName,
        match.Descriptor.Description,
        match.Descriptor.UseCases,
        match.Descriptor.Limitations,
        inputs = match.Descriptor.Inputs.Select(port => new { port.Name, port.Type, port.Required }),
        outputs = match.Descriptor.Outputs.Select(port => new { port.Name, port.Type }),
        parameters = match.Descriptor.Parameters.Select(parameter => new {
          parameter.Name,
          parameter.Kind,
          parameter.DefaultValue,
          parameter.Minimum,
          parameter.Maximum,
          parameter.Description
        })
      })
        ,
      recommendedPipeline = isCrackComposition
            ? [$"SOURCE({request.SelectedNodeId})", "Image.Gray", "Image.GaussianBlur", "Morphology.BlackHat", "Threshold.Otsu", "Morphology.Close", "Blob.Contours", "Contour.PathMetrics(Image from SOURCE, Contours from Blob.Contours)"]
            : Array.Empty<string>(),
      compactPlanTemplate = isCrackComposition
            ? new {
              summary = "使用通用 OpenCV 多阶段流程测量细长缺陷的数量与像素长度",
              targetNodeId = crackNodeIds[^1],
              actions = new[]
                {
                        new
                        {
                            type = "add_pipeline",
                            fromNode = request.SelectedNodeId,
                            operatorIds = new[] { "Image.Gray", "Image.GaussianBlur", "Morphology.BlackHat", "Threshold.Otsu", "Morphology.Close", "Blob.Contours", "Contour.PathMetrics" },
                            nodeIds = crackNodeIds
                        }
                }
            }
            : null
    }, PromptJsonOptions);
    var messages = new List<AiChatMessage>();
    if (request.Conversation is { Count: > 0 }) {
      messages.AddRange(request.Conversation.TakeLast(20));
    }

    messages.Add(new("user", context));
    AiWorkflowPlan? plan = null;
    Exception? lastPlanError = null;
    string? lastPlanContent = null;
    for (int attempt = 0; attempt < 3; attempt++) {
      var modelResponse = await provider.SendAsync(new(SystemPrompt, messages, request.PreviewImagePath), cancellationToken);
      lastPlanContent = modelResponse.Content;
      try {
        plan = ParsePlan(modelResponse.Content);
        plan = BindPipelineSource(plan, request.SelectedNodeId);
        plan = NormalizePlan(ExpandPipelineActions(plan, catalog, snapshot.Workflow));
        ValidatePlan(plan, catalog, snapshot.Workflow, request.SelectedNodeId);
        ValidatePlanningPolicy(plan, snapshot.Workflow, request.Text);
        break;
      } catch (Exception exception) when (exception is JsonException or InvalidDataException) {
        plan = null;
        lastPlanError = exception;
        messages.Add(new("assistant", modelResponse.Content.Length <= 1500 ? modelResponse.Content : modelResponse.Content[..1500]));
        string correction = isCrackComposition
            ? $"The plan was rejected: {exception.Message}. Copy compactPlanTemplate from the original context as one complete JSON object. Return exactly one add_pipeline action; do not write add_node or connect_nodes actions."
            : $"The plan was rejected: {exception.Message}. Return a complete corrected JSON object only. Keep nodeId instance aliases separate from operatorId interface names and build the requested result from an inspectable multi-stage pipeline.";
        messages.Add(new("user", correction));
      }
    }
    if (plan is null) {
      throw new InvalidDataException(
          $"AI did not return a valid workflow plan after three attempts.\n" +
          $"Validation error: {lastPlanError?.Message ?? "unknown validation error"}\n" +
          $"Last model response:\n```json\n{LimitWithNotice(lastPlanContent, 4000)}\n```",
          lastPlanError);
    }

    if (request.Mode == AiAssistantMode.Suggest) {
      return new(BuildSuggestionMessage(plan, catalog), plan);
    }

    return await ApplyPlanAsync(plan, snapshot.Revision, cancellationToken);
  }

  public async Task<AiAssistantResponse> ApplyPlanAsync(AiWorkflowPlan plan, long expectedRevision, CancellationToken cancellationToken = default) {
    var snapshot = await workflow.GetSnapshotAsync(cancellationToken);
    var catalog = await workflow.GetOperatorCatalogAsync(cancellationToken);
    plan = NormalizePlan(ExpandPipelineActions(plan, catalog, snapshot.Workflow));
    if (snapshot.Revision != expectedRevision) {
      throw new WorkflowRevisionConflictException(expectedRevision, snapshot.Revision);
    }

    try { ValidatePlan(plan, catalog, snapshot.Workflow, selectedNodeId: null); } catch (InvalidDataException exception) {
      throw new InvalidDataException($"{exception.Message} Normalized plan: {Limit(JsonSerializer.Serialize(plan, WorkflowJson.Options), 2000)}", exception);
    }
    var edits = Compile(plan, snapshot.Workflow);
    var mutation = await workflow.ApplyAsync(new($"ai-{Guid.NewGuid():N}", snapshot.Revision, edits, plan.Summary, plan.TargetNodeId, plan.TargetNodeId), cancellationToken);
    var execution = mutation.Execution;
    string message = execution is { Success: true }
        ? string.Join("；", execution.Outputs.Where(output => output.Port is not "Image").Select(output => $"{output.Port}: {output.Summary}"))
        : execution?.Error ?? "AI 已应用工作流方案。";
    return new(message, plan, mutation, execution);
  }

  public static AiWorkflowPlan ParsePlan(string content) {
    int start = content.IndexOf('{');
    int end = content.LastIndexOf('}');
    if (start < 0 || end <= start) {
      throw new InvalidDataException("AI response does not contain a JSON plan.");
    }

    string json = content[start..(end + 1)];
    using var document = JsonDocument.Parse(json);
    AiWorkflowPlan? plan;
    if (document.RootElement.TryGetProperty("type", out _)) {
      var action = JsonSerializer.Deserialize<AiGraphAction>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
          ?? throw new InvalidDataException("AI response action is empty.");
      plan = new("AI 工作流方案", [action], action.NodeIds?.LastOrDefault() ?? action.NodeId);
    } else {
      plan = JsonSerializer.Deserialize<AiWorkflowPlan>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
          ?? throw new InvalidDataException("AI response plan is empty.");
    }
    if (plan.Actions is not { Count: > 0 }) {
      throw new InvalidDataException("AI response plan is incomplete.");
    }

    if (plan.Actions.Count > 24) {
      throw new InvalidDataException("AI plan exceeds the maximum action count of 24.");
    }

    return NormalizePlan(plan);
  }

  private static string BuildSuggestionMessage(AiWorkflowPlan plan, IReadOnlyList<OperatorDescriptor> catalog) {
    var descriptors = catalog.ToDictionary(item => item.Type, StringComparer.Ordinal);
    var parameterValues = plan.Actions.Where(action => action.Type == "set_parameter")
        .GroupBy(action => (action.NodeId!, action.Parameter!))
        .ToDictionary(group => group.Key, group => group.Last().Value);
    var lines = new List<string> { $"建议方案：{plan.Summary}", "", "节点与参数：" };
    int index = 0;
    foreach (var action in plan.Actions.Where(action => action.Type == "add_node")) {
      index++;
      if (!descriptors.TryGetValue(action.OperatorId ?? "", out var descriptor)) {
        lines.Add($"{index}. {action.NodeId} · {action.OperatorId}");
        continue;
      }
      string[] parameters = descriptor.Parameters.Select(parameter => {
        object? value = parameterValues.TryGetValue((action.NodeId!, parameter.Name), out object? configured)
            ? configured
            : parameter.DefaultValue;
        return $"{parameter.Name}={FormatParameter(value)}";
      }).ToArray();
      lines.Add($"{index}. {descriptor.DisplayName} [{action.NodeId}] · 接口 {descriptor.Type}");
      lines.Add($"   参数：{(parameters.Length == 0 ? "无" : string.Join("，", parameters))}");
    }
    lines.Add("");
    lines.Add("连接：");
    foreach (var action in plan.Actions.Where(action => action.Type == "connect_nodes")) {
      lines.Add($"- {action.FromNode}.{action.FromPort} → {action.ToNode}.{action.ToPort}");
    }

    lines.Add("");
    lines.Add($"结果节点：{plan.TargetNodeId}");
    return string.Join(Environment.NewLine, lines);
  }

  private static string FormatParameter(object? value) {
    if (value is null) {
      return "null";
    }

    if (value is JsonElement element) {
      return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
    }

    return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
  }

  private static AiWorkflowPlan NormalizePlan(AiWorkflowPlan plan) {
    string summary = string.IsNullOrWhiteSpace(plan.Summary) ? "AI 工作流方案" : plan.Summary;
    var normalizedActions = NormalizeNodeReferences(plan.Actions);
    var replacements = normalizedActions
        .Where(action => action.Type == "add_node" && !string.IsNullOrWhiteSpace(action.OperatorId) && !string.IsNullOrWhiteSpace(action.NodeId))
        .GroupBy(action => action.OperatorId!, StringComparer.Ordinal)
        .Where(group => group.Count() == 1)
        .ToDictionary(group => group.Key, group => group.Single().NodeId!, StringComparer.Ordinal);
    string? normalizedTarget = replacements.TryGetValue(plan.TargetNodeId ?? "", out string? targetAlias) ? targetAlias : plan.TargetNodeId;
    string? targetNodeId = string.IsNullOrWhiteSpace(normalizedTarget)
        ? normalizedActions.LastOrDefault(action => action.Type == "add_node")?.NodeId
        : normalizedTarget;
    var orderedActions = normalizedActions.OrderBy(action => action.Type switch {
      "add_node" or "add_pipeline" => 0,
      "set_parameter" => 1,
      "connect_nodes" => 2,
      "remove_node" => 3,
      _ => 4
    }).ToList();
    return plan with { Summary = summary, TargetNodeId = targetNodeId, Actions = orderedActions };
  }

  private static AiWorkflowPlan BindPipelineSource(AiWorkflowPlan plan, string? selectedNodeId) {
    if (string.IsNullOrWhiteSpace(selectedNodeId)) {
      return plan;
    }

    var actions = plan.Actions.Select(action => action.Type == "add_pipeline"
        ? action with { FromNode = selectedNodeId }
        : action).ToList();
    return plan with { Actions = actions };
  }

  private static AiWorkflowPlan ExpandPipelineActions(AiWorkflowPlan plan, IReadOnlyList<OperatorDescriptor> catalog, WorkflowDocument workflowDocument) {
    if (plan.Actions.All(action => action.Type != "add_pipeline")) {
      return plan;
    }

    var descriptors = catalog.ToDictionary(item => item.Type, StringComparer.Ordinal);
    var nodeTypes = workflowDocument.Nodes.ToDictionary(node => node.Id, node => node.Type, StringComparer.Ordinal);
    var expanded = new List<AiGraphAction>();
    var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
    string? lastPipelineNode = null;

    foreach (var action in plan.Actions) {
      if (action.Type != "add_pipeline") {
        expanded.Add(RemapNodeReferences(action, replacements));
        continue;
      }

      var operatorIds = action.OperatorIds ?? [];
      var nodeIds = action.NodeIds ?? [];
      if (operatorIds.Count == 0 || operatorIds.Count != nodeIds.Count) {
        throw new InvalidDataException("AI add_pipeline requires equally sized non-empty operatorIds and nodeIds arrays.");
      }

      if (string.IsNullOrWhiteSpace(action.FromNode) || !nodeTypes.ContainsKey(action.FromNode)) {
        throw new InvalidDataException($"AI pipeline source node '{action.FromNode}' does not exist.");
      }

      string previousNode = action.FromNode;
      var previousDescriptor = descriptors[nodeTypes[previousNode]];
      for (int index = 0; index < operatorIds.Count; index++) {
        string operatorId = operatorIds[index];
        string requestedNodeId = nodeIds[index];
        if (string.IsNullOrWhiteSpace(requestedNodeId)) {
          throw new InvalidDataException("AI pipeline node id is empty.");
        }

        string nodeId = CreateUniqueNodeId(requestedNodeId, nodeTypes.Keys);
        if (!string.Equals(requestedNodeId, nodeId, StringComparison.Ordinal)) {
          replacements[requestedNodeId] = nodeId;
        }

        if (!descriptors.TryGetValue(operatorId, out var targetDescriptor)) {
          throw new InvalidDataException($"AI pipeline operator '{operatorId}' does not exist.");
        }

        expanded.Add(new("add_node", NodeId: nodeId, OperatorId: operatorId));
        nodeTypes.Add(nodeId, operatorId);
        foreach (var input in targetDescriptor.Inputs.Where(input => input.Required)) {
          var output = (previousDescriptor.Outputs.FirstOrDefault(candidate => candidate.Name == input.Name && PortsCompatible(candidate, input))
              ?? previousDescriptor.Outputs.FirstOrDefault(candidate => PortsCompatible(candidate, input))) ?? throw new InvalidDataException($"AI pipeline cannot connect '{previousDescriptor.Type}' to required input '{operatorId}.{input.Name}'.");
          expanded.Add(new("connect_nodes", FromNode: previousNode, FromPort: output.Name, ToNode: nodeId, ToPort: input.Name));
        }
        previousNode = nodeId;
        previousDescriptor = targetDescriptor;
        lastPipelineNode = nodeId;
      }
    }

    string? requestedTarget = replacements.TryGetValue(plan.TargetNodeId ?? "", out string? remappedTarget) ? remappedTarget : plan.TargetNodeId;
    string? targetNodeId = string.IsNullOrWhiteSpace(requestedTarget) || plan.Actions.Any(action => action.Type == "add_pipeline" && action.OperatorIds?.Contains(requestedTarget) == true)
        ? lastPipelineNode
        : requestedTarget;
    if (expanded.Count > 24) {
      throw new InvalidDataException("Expanded AI pipeline exceeds the maximum action count of 24.");
    }

    return plan with { Actions = expanded, TargetNodeId = targetNodeId };
  }

  private static bool PortsCompatible(PortDefinition output, PortDefinition input) {
    return input.Type == NodeValueType.Any || output.Type == NodeValueType.Any || input.Type == output.Type;
  }

  private static AiGraphAction RemapNodeReferences(AiGraphAction action, IReadOnlyDictionary<string, string> replacements) {
    return action with {
      NodeId = replacements.TryGetValue(action.NodeId ?? "", out string? nodeId) ? nodeId : action.NodeId,
      FromNode = replacements.TryGetValue(action.FromNode ?? "", out string? fromNode) ? fromNode : action.FromNode,
      ToNode = replacements.TryGetValue(action.ToNode ?? "", out string? toNode) ? toNode : action.ToNode
    };
  }

  private static string[] CreateUniqueNodeIds(WorkflowDocument workflowDocument, IReadOnlyList<string> stems) {
    var used = workflowDocument.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
    return [.. stems.Select(stem => {
      string nodeId = CreateUniqueNodeId(stem, used);
      used.Add(nodeId);
      return nodeId;
    })];
  }

  private static string CreateUniqueNodeId(string requested, IEnumerable<string> existing) {
    var used = existing as ISet<string> ?? existing.ToHashSet(StringComparer.Ordinal);
    if (!used.Contains(requested)) {
      return requested;
    }

    for (int suffix = 2; ; suffix++) {
      string candidate = $"{requested}_{suffix}";
      if (!used.Contains(candidate)) {
        return candidate;
      }
    }
  }

  private static IReadOnlyList<AiGraphAction> NormalizeNodeReferences(IReadOnlyList<AiGraphAction> actions) {
    var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
    var normalized = new List<AiGraphAction>(actions.Count);
    var addedByOperator = actions.Where(action => action.Type == "add_node" && !string.IsNullOrWhiteSpace(action.OperatorId))
        .GroupBy(action => action.OperatorId!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    int index = 0;
    foreach (var action in actions) {
      if (action.Type != "add_node") { normalized.Add(action); continue; }
      index++;
      string? original = action.NodeId;
      string? nodeId = original;
      if (string.IsNullOrWhiteSpace(nodeId) || string.Equals(nodeId, action.OperatorId, StringComparison.Ordinal)) {
        string suffix = new string([.. (action.OperatorId ?? "node").Split('.').Last().Where(char.IsLetterOrDigit)]).ToLowerInvariant();
        nodeId = $"ai_{(string.IsNullOrWhiteSpace(suffix) ? "node" : suffix)}_{index}";
      }
      if (!string.IsNullOrWhiteSpace(original)) {
        replacements[original] = nodeId;
      }

      if (!string.IsNullOrWhiteSpace(action.OperatorId) && addedByOperator[action.OperatorId] == 1) {
        replacements[action.OperatorId] = nodeId;
      }

      normalized.Add(action with { NodeId = nodeId });
    }
    return [.. normalized.Select(action => action with {
      NodeId = action.Type == "set_parameter" && replacements.TryGetValue(action.NodeId ?? "", out string? parameterNode) ? parameterNode : action.NodeId,
      FromNode = replacements.TryGetValue(action.FromNode ?? "", out string? fromNode) ? fromNode : action.FromNode,
      ToNode = replacements.TryGetValue(action.ToNode ?? "", out string? toNode) ? toNode : action.ToNode
    })];
  }

  private static IReadOnlyList<OperatorSearchResult> PlanningOperators(IReadOnlyList<OperatorDescriptor> catalog, string request) {
    if (IsCrackCompositionRequest(request)) {
      string[] required = new[] { "Image.Gray", "Image.GaussianBlur", "Morphology.BlackHat", "Threshold.Otsu", "Morphology.Close", "Blob.Contours", "Contour.PathMetrics" };
      var matches = new List<OperatorSearchResult>();
      foreach (string? type in required) {
        var descriptor = catalog.FirstOrDefault(item => item.Type == type);
        if (descriptor is not null) {
          matches.Add(new(descriptor, double.MaxValue, ["recommended-pipeline"]));
        }
      }
      if (ExplicitSpecializedRequest(request)) {
        var specialized = catalog.FirstOrDefault(item => item.Type == "Defect.CrackMeasure");
        if (specialized is not null) {
          matches.Add(new(specialized, double.MaxValue, ["explicit-specialized-request"]));
        }
      }
      return matches;
    }
    return new OperatorSearchService().Search(catalog, request, 12);
  }

  private static void ValidatePlanningPolicy(AiWorkflowPlan plan, WorkflowDocument workflowDocument, string request) {
    if (!IsCrackCompositionRequest(request) || ExplicitSpecializedRequest(request)) {
      return;
    }

    var operatorTypes = workflowDocument.Nodes.Select(node => node.Type)
        .Concat(plan.Actions.Where(action => action.Type == "add_node").Select(action => action.OperatorId ?? ""))
        .ToHashSet(StringComparer.Ordinal);
    if (plan.Actions.Any(action => action.Type == "add_node" && action.OperatorId == "Defect.CrackMeasure")) {
      throw new InvalidDataException("The specialized Defect.CrackMeasure operator was not requested; compose the solution from general OpenCV stages.");
    }

    foreach (string? requiredStage in new[] { "Threshold.Otsu", "Blob.Contours", "Contour.PathMetrics" }) {
      if (!operatorTypes.Contains(requiredStage)) {
        throw new InvalidDataException($"The composed crack workflow is missing required stage '{requiredStage}'.");
      }
    }
  }

  private static bool IsCrackCompositionRequest(string text) {
    return (text.Contains("裂纹", StringComparison.OrdinalIgnoreCase) || text.Contains("裂缝", StringComparison.OrdinalIgnoreCase) || text.Contains("crack", StringComparison.OrdinalIgnoreCase)) &&
      (text.Contains("数量", StringComparison.OrdinalIgnoreCase) || text.Contains("长度", StringComparison.OrdinalIgnoreCase) || text.Contains("count", StringComparison.OrdinalIgnoreCase) || text.Contains("length", StringComparison.OrdinalIgnoreCase));
  }

  private static bool ExplicitSpecializedRequest(string text) {
    return text.Contains("Defect.CrackMeasure", StringComparison.OrdinalIgnoreCase) ||
      text.Contains("专用裂纹算子", StringComparison.OrdinalIgnoreCase) || text.Contains("单算子", StringComparison.OrdinalIgnoreCase);
  }

  private static void ValidatePlan(AiWorkflowPlan plan, IReadOnlyList<OperatorDescriptor> catalog, WorkflowDocument workflowDocument, string? selectedNodeId) {
    var descriptors = catalog.ToDictionary(item => item.Type, StringComparer.Ordinal);
    var nodes = workflowDocument.Nodes.ToDictionary(node => node.Id, node => node.Type, StringComparer.Ordinal);
    var addedNodes = new HashSet<string>(StringComparer.Ordinal);
    var occupiedInputs = workflowDocument.Connections
        .Select(connection => (connection.ToNode, connection.ToPort))
        .ToHashSet();
    if (!string.IsNullOrWhiteSpace(selectedNodeId) && !nodes.ContainsKey(selectedNodeId)) {
      throw new InvalidDataException("Selected node does not exist.");
    }

    foreach (var action in plan.Actions) {
      if (action.Type is not ("add_node" or "remove_node" or "connect_nodes" or "set_parameter")) {
        throw new InvalidDataException($"AI action '{action.Type}' is not allowed.");
      }

      if (action.Type == "add_node") {
        if (string.IsNullOrWhiteSpace(action.NodeId) || string.IsNullOrWhiteSpace(action.OperatorId) || !descriptors.ContainsKey(action.OperatorId)) {
          throw new InvalidDataException("AI add_node action is invalid.");
        }

        if (!nodes.TryAdd(action.NodeId, action.OperatorId)) {
          throw new InvalidDataException($"AI node id '{action.NodeId}' is duplicated.");
        }

        addedNodes.Add(action.NodeId);
      } else if (action.Type == "remove_node") {
        RequireNode(action.NodeId, nodes);
        if (action.NodeId == selectedNodeId) {
          throw new InvalidDataException("AI cannot remove the user's selected input node.");
        }

        nodes.Remove(action.NodeId!);
        addedNodes.Remove(action.NodeId!);
        occupiedInputs.RemoveWhere(input => input.ToNode == action.NodeId);
      } else if (action.Type == "connect_nodes") {
        RequireNode(action.FromNode, nodes);
        RequireNode(action.ToNode, nodes);
        var source = descriptors[nodes[action.FromNode!]];
        var target = descriptors[nodes[action.ToNode!]];
        var output = source.Outputs.SingleOrDefault(port => port.Name == action.FromPort)
            ?? throw new InvalidDataException($"AI source port '{action.FromPort}' does not exist.");
        var input = target.Inputs.SingleOrDefault(port => port.Name == action.ToPort)
            ?? throw new InvalidDataException($"AI target port '{action.ToPort}' does not exist.");
        if (input.Type != NodeValueType.Any && output.Type != NodeValueType.Any && input.Type != output.Type) {
          throw new InvalidDataException("AI connection has incompatible port types.");
        }

        if (!occupiedInputs.Add((action.ToNode!, action.ToPort!))) {
          throw new InvalidDataException($"AI input '{action.ToNode}.{action.ToPort}' has more than one connection.");
        }
      } else if (action.Type == "set_parameter") {
        RequireNode(action.NodeId, nodes);
        var descriptor = descriptors[nodes[action.NodeId!]];
        if (descriptor.Parameters.All(parameter => parameter.Name != action.Parameter)) {
          throw new InvalidDataException($"AI parameter '{action.Parameter}' does not exist.");
        }

        if (action.Value is null) {
          throw new InvalidDataException("AI set_parameter action has no value.");
        }
      }
    }
    foreach (string nodeId in addedNodes) {
      var descriptor = descriptors[nodes[nodeId]];
      foreach (var input in descriptor.Inputs.Where(input => input.Required)) {
        if (!occupiedInputs.Contains((nodeId, input.Name))) {
          throw new InvalidDataException($"AI required input '{nodeId}.{input.Name}' is not connected.");
        }
      }
    }
    if (!string.IsNullOrWhiteSpace(plan.TargetNodeId)) {
      RequireNode(plan.TargetNodeId, nodes);
    }
  }

  private static void RequireNode(string? nodeId, IReadOnlyDictionary<string, string> nodes) {
    if (string.IsNullOrWhiteSpace(nodeId) || !nodes.ContainsKey(nodeId)) {
      throw new InvalidDataException($"AI node '{nodeId}' does not exist.");
    }
  }

  private static string Limit(string? value, int maximum) {
    return string.IsNullOrEmpty(value) || value.Length <= maximum ? value ?? "" : value[..maximum];
  }

  private static string LimitWithNotice(string? value, int maximum) {
    if (string.IsNullOrEmpty(value) || value.Length <= maximum) {
      return value ?? "";
    }

    return value[..maximum] + $"\n... [response truncated, total {value.Length} characters]";
  }

  private static IReadOnlyList<WorkflowEdit> Compile(AiWorkflowPlan plan, WorkflowDocument workflowDocument) {
    var positions = PlanPositions(plan, workflowDocument);
    return [.. plan.Actions.Select<AiGraphAction, WorkflowEdit>(action => action.Type switch {
      "add_node" => new AddNodeEdit(action.NodeId!, action.OperatorId!, X: positions[action.NodeId!].X, Y: positions[action.NodeId!].Y),
      "remove_node" => new RemoveNodeEdit(action.NodeId!),
      "connect_nodes" => new ConnectEdit(action.FromNode!, action.FromPort!, action.ToNode!, action.ToPort!),
      "set_parameter" => new SetParameterEdit(action.NodeId!, action.Parameter!, JsonSerializer.SerializeToElement(action.Value, WorkflowJson.Options)),
      _ => throw new InvalidDataException($"Unsupported AI action '{action.Type}'.")
    })];
  }

  private static IReadOnlyDictionary<string, (double X, double Y)> PlanPositions(AiWorkflowPlan plan, WorkflowDocument workflowDocument) {
    var added = plan.Actions.Where(action => action.Type == "add_node").Select(action => action.NodeId!).ToHashSet(StringComparer.Ordinal);
    var levels = added.ToDictionary(nodeId => nodeId, _ => 1, StringComparer.Ordinal);
    var incoming = plan.Actions.Where(action => action.Type == "connect_nodes" && added.Contains(action.ToNode!)).ToList();
    for (int pass = 0; pass < added.Count; pass++) {
      foreach (var edge in incoming) {
        if (added.Contains(edge.FromNode!)) {
          levels[edge.ToNode!] = Math.Max(levels[edge.ToNode!], levels[edge.FromNode!] + 1);
        }
      }
    }

    var sourceIds = incoming.Where(edge => !added.Contains(edge.FromNode!)).Select(edge => edge.FromNode!).Distinct(StringComparer.Ordinal).ToList();
    var sources = workflowDocument.Nodes.Where(node => sourceIds.Contains(node.Id, StringComparer.Ordinal)).ToList();
    double originX = sources.Count > 0 ? sources.Max(node => node.X) : workflowDocument.Nodes.Select(node => node.X).DefaultIfEmpty(60).Max();
    double originY = sources.Count > 0 ? sources.Min(node => node.Y) : 60;
    var result = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
    foreach (var group in levels.GroupBy(pair => pair.Value).OrderBy(group => group.Key)) {
      int row = 0;
      foreach (var node in group.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
        var position = (X: originX + group.Key * 280, Y: originY + row * 210);
        while (workflowDocument.Nodes.Any(existing => Math.Abs(existing.X - position.X) < 220 && Math.Abs(existing.Y - position.Y) < 170) ||
               result.Values.Any(existing => Math.Abs(existing.X - position.X) < 220 && Math.Abs(existing.Y - position.Y) < 170)) {
          position.Y += 210;
        }

        result[node.Key] = position;
        row++;
      }
    }
    return result;
  }
}
