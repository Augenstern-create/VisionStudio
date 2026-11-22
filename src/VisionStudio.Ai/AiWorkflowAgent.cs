using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VisionStudio.Ai.Abstractions;
using VisionStudio.Core;

namespace VisionStudio.Ai;

public sealed class AiWorkflowAgent(IWorkflowAutomationApi workflow, IAiProvider provider, IAiWorkflowTools? tools = null) {
  /// <summary>Upper bound on emitted actions; a 10-node pipeline with connections and parameters fits.</summary>
  public const int MaximumActions = 48;
  public const int MaximumNodes = 32;
  private static readonly JsonSerializerOptions PromptJsonOptions = new(WorkflowJson.Options) {
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  private const string SystemPrompt = """
You are Vision Studio's workflow assistant, talking with a vision engineer about the image-processing graph they are building.
Answer in the same language the user writes in. Use plain natural language, be concise and concrete, and refer to operators by their catalog names.

You have two jobs:
1. Explain and advise. Describe what an operator does, why a preview looks the way it does, which parameters to try, and how to restructure a pipeline. Base every statement on the supplied graph, catalog and recommendedOperators; never invent operators, ports or parameters that are not listed.
2. Propose graph changes when the user asks you to build or modify the workflow. In that case append exactly one fenced JSON block at the very end of your reply, after your prose:

```json
{"summary":"one line describing the change","targetNodeId":"...","actions":[...]}
```

Never put prose inside the JSON block and never emit more than one block. If the user only asks a question, emit no JSON block at all. Keep the answer itself useful on its own: the card is supplementary, not a replacement for your explanation.

Allowed action types: add_pipeline, add_node, remove_node, connect_nodes, disconnect_nodes, set_parameter.
To insert a node into an existing edge, emit disconnect_nodes for the old edge, then add_node plus the two connect_nodes actions.
- For a new sequential pipeline prefer one compact action: {"type":"add_pipeline","fromNode":"EXISTING_SOURCE_NODE_ID","operatorIds":["Image.Gray","Image.GaussianBlur"],"nodeIds":["gray","blur"],"parameters":{"blur":{"kernelSize":5}}}. The host expands it, connects compatible ports and applies the parameters map (keyed by the alias in nodeIds). operatorIds and nodeIds must have equal lengths; targetNodeId is the last nodeIds value. Carrying parameters inside add_pipeline keeps a ten-node proposal to a single action, so do not split a pipeline into add_node/connect_nodes pairs unless you must edit existing nodes.
- add_node needs nodeId (short unique alias such as ai_gray_1) plus operatorId (the catalog interface such as Image.Gray). Never use an operatorId as a node id.
- connect_nodes needs fromNode, fromPort, toNode and toPort. Every required input of every new node must have exactly one incoming connection; an output may fan out.
- set_parameter needs nodeId, parameter and value.
Keep proposals under 48 actions and 32 nodes, reuse compatible existing nodes when practical, and never emit C#, shell commands or a complete workflow file.

The proposal is rendered as a review card and applied only when the user accepts it. Never claim a change has already been applied.

## Running a node for real numbers
The graph context lists every node with its parameters. When your answer depends on a value you cannot see (an output count, a fitted radius, a threshold value), reply with exactly one fenced block and nothing else:

```json
{"tool":"execute_node","nodeId":"<node id from the graph>"}
```

You then receive a `[tool result]` message with the node's outputs and can continue.

To compare parameter values, request a sweep instead of repeating execute_node:

```json
{"tool":"sweep_parameter","nodeId":"otsu","parameter":"threshold","from":60,"to":140,"step":20,"targetNodeId":"contours"}
```

`values` may replace `from`/`to`/`step`. `targetNodeId` defaults to the swept node; set it when the interesting number lives downstream (for example sweep a threshold but measure the contour count). At most 12 values per sweep.

Limits: at most 3 tool calls per reply, only existing node ids and parameter names, and only when the measurement changes your advice. Never invent measurements and never claim you ran something you did not run.

When the user's question depends on a numeric parameter whose effect you cannot predict (threshold, kernel size, min area, tolerance), run one sweep of 3-5 values first and answer from the numbers instead of asking the user to try things. Do not sweep for purely conceptual questions.
""";

  public async Task<AiAssistantResponse> ProcessAsync(
      AiAssistantRequest request,
      IProgress<AiStreamDelta>? progress = null,
      IProgress<string>? toolProgress = null,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
    var snapshot = await workflow.GetSnapshotAsync(cancellationToken);
    var catalog = await workflow.GetOperatorCatalogAsync(cancellationToken);
    var matches = PlanningOperators(catalog, request.Text);
    bool isCrackComposition = IsCrackCompositionRequest(request.Text);
    string[] crackNodeIds = isCrackComposition
        ? CreateUniqueNodeIds(snapshot.Workflow, ["gray", "blur", "blackhat", "otsu", "close", "contours", "metrics"])
        : [];
    string BuildContext(int detail) {
      var recommended = detail == 2
          ? (object)matches.Select(match => new {
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
          : matches.Select(match => new {
            id = match.Descriptor.Type,
            name = match.Descriptor.DisplayName,
            inputs = match.Descriptor.Inputs.Select(port => new { port.Name, port.Type, port.Required }),
            outputs = match.Descriptor.Outputs.Select(port => new { port.Name, port.Type }),
            parameters = match.Descriptor.Parameters.Select(parameter => new {
              parameter.Name,
              parameter.Kind,
              parameter.DefaultValue,
              parameter.Minimum,
              parameter.Maximum
            })
          });

      return JsonSerializer.Serialize(new {
        requirement = request.Text,
        selectedNodeId = request.SelectedNodeId,
        graph = new {
          snapshot.Revision,
          nodes = snapshot.Workflow.Nodes.Select(node => new {
            node.Id,
            node.Type,
            node.Name,
            parameters = node.Parameters
          }),
          connections = snapshot.Workflow.Connections,
          selectedNode = request.SelectedNodeId is null
              ? null
              : snapshot.Workflow.Nodes
                  .Where(node => node.Id == request.SelectedNodeId)
                  .Select(node => new { node.Id, node.Type, node.Name, parameters = node.Parameters })
                  .FirstOrDefault()
        },
        operatorCount = catalog.Count,
        operators = catalog.Select(descriptor => $"{descriptor.Type}={descriptor.DisplayName}"),
        recommendedOperators = detail == 0 ? Array.Empty<object>() : recommended,
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
    }

    // Bound the prompt by the profile's context window: reserve room for the answer, then
    // degrade the catalog detail and history until the estimate fits.
    int inputBudget = Math.Max(4000, provider.Profile.MaxInputTokens) - Math.Max(1024, provider.Profile.MaximumOutputTokens);
    int historyBudget = Math.Max(512, inputBudget / 4);
    int contextBudget = Math.Max(1500, inputBudget - historyBudget);

    string context = BuildContext(2);
    if (EstimateTokens(context) > contextBudget) {
      context = BuildContext(1);
    }

    if (EstimateTokens(context) > contextBudget) {
      context = BuildContext(0);
    }

    var messages = new List<AiChatMessage>();
    // Errors and execution summaries are UI-only: replaying them as assistant turns teaches the
    // model that it said things it never said.
    var conversation = request.Conversation?
        .Where(message => string.Equals(message.Kind, AiChatMessageKinds.Chat, StringComparison.Ordinal))
        .ToList();
    messages.AddRange(TrimHistory(conversation, historyBudget));
    messages.Add(new("user", context));

    const int maximumExecutions = 12;
    const int maximumToolCalls = 3;
    int executions = 0;
    int toolCalls = 0;
    string lastProse = "";
    for (int round = 0; round <= maximumToolCalls; round++) {
      var answer = await SendAsync(
          new(SystemPrompt, messages, request.PreviewImagePath, RequireJson: false, Temperature: .3), progress, cancellationToken);
      var (prose, proposal) = SplitProposal(answer.Content);
      lastProse = prose;

      // Read-only tool call: the model asks to run one node and receives its measurements.
      if (proposal is not null && TryReadSweepCall(proposal, out AiParameterSweep? sweep, out string? sweepProblem)) {
        if (sweepProblem is not null) {
          messages.Add(new("assistant", answer.Content));
          messages.Add(new("user", $"[tool error] {sweepProblem}"));
          continue;
        }

        if (toolCalls >= maximumToolCalls || executions + sweep!.Values.Count > maximumExecutions) {
          messages.Add(new("user", $"[tool limit] Tool budget exhausted ({maximumToolCalls} calls / {maximumExecutions} node executions per reply). Answer with what you already have."));
          continue;
        }

        if (tools is null) {
          messages.Add(new("user", "[tool error] parameter sweeps are unavailable in this host"));
          continue;
        }

        var sweepRequest = sweep!;
        toolCalls++;
        executions += sweepRequest.Values.Count;
        toolProgress?.Report($"sweep:{sweepRequest.NodeId}.{sweepRequest.Parameter}");
        string sweepSummary = await tools.SweepParameterAsync(sweepRequest, cancellationToken);
        messages.Add(new("assistant", answer.Content));
        messages.Add(new("user", $"[tool result] sweep {sweepRequest.NodeId}.{sweepRequest.Parameter}\n{sweepSummary}"));
        continue;
      }

      if (proposal is not null && TryReadToolCall(proposal, out string? requestedNode, out string? toolProblem)) {
        if (toolProblem is not null) {
          messages.Add(new("assistant", answer.Content));
          messages.Add(new("user", $"[tool error] {toolProblem}"));
          continue;
        }

        string nodeId = requestedNode!;
        if (toolCalls >= maximumToolCalls || executions + 1 > maximumExecutions) {
          messages.Add(new("user", $"[tool limit] Tool budget exhausted ({maximumToolCalls} calls / {maximumExecutions} node executions per reply). Answer with what you already have."));
          continue;
        }

        toolCalls++;
        executions++;
        toolProgress?.Report(nodeId);
        string summary = tools is not null
            ? await tools.ExecuteNodeAsync(nodeId, cancellationToken)
            : await ExecuteNodeAsync(nodeId, request.SelectedNodeId, cancellationToken);
        messages.Add(new("assistant", answer.Content));
        messages.Add(new("user", $"[tool result] node={nodeId}\n{summary}"));
        continue;
      }

      if (proposal is null) {
        return new(AnnotateUnknownOperators(string.IsNullOrWhiteSpace(prose) ? "（模型没有返回内容。）" : prose, catalog));
      }

    string currentProse = prose;
    string currentProposal = proposal;
    Exception? lastProposalError = null;
    for (int attempt = 0; attempt < 2; attempt++) {
      try {
        var plan = ParsePlan(currentProposal);
        plan = BindPipelineSource(plan, request.SelectedNodeId);
        plan = NormalizePlan(ExpandPipelineActions(plan, catalog, snapshot.Workflow));
        ValidatePlan(plan, catalog, snapshot.Workflow, request.SelectedNodeId);
        ValidatePlanningPolicy(plan, snapshot.Workflow, request.Text);
        return new(AnnotateUnknownOperators(string.IsNullOrWhiteSpace(currentProse) ? plan.Summary : currentProse, catalog), plan);
      } catch (Exception exception) when (exception is JsonException or InvalidDataException) {
        lastProposalError = exception;
        if (attempt == 1) {
          break;
        }

        messages.Add(new("assistant", currentProposal));
        messages.Add(new("user",
            $"That proposal was rejected: {exception.Message}\n" +
            "Reply with only a corrected JSON block, or with plain text and no JSON block when no safe change is possible."));
        var repair = await SendAsync(
            new(SystemPrompt, messages, request.PreviewImagePath, RequireJson: false, Temperature: .1), progress: null, cancellationToken);
        var repaired = SplitProposal(repair.Content);
        if (!string.IsNullOrWhiteSpace(repaired.Prose)) {
          currentProse = repaired.Prose;
        }

        if (repaired.Proposal is null) {
          return new(currentProse);
        }

        currentProposal = repaired.Proposal;
      }
    }

    string notice = $"{Environment.NewLine}{Environment.NewLine}（改动建议未通过校验，已忽略：{lastProposalError?.Message}）";
    return new(currentProse + notice);
    }

    return new(string.IsNullOrWhiteSpace(lastProse) ? "（模型没有返回内容。）" : lastProse);
  }

  private static readonly System.Text.RegularExpressions.Regex OperatorMention = new(
      @"\b[A-Z][A-Za-z0-9]+\.[A-Za-z][A-Za-z0-9]*\b",
      System.Text.RegularExpressions.RegexOptions.Compiled);

  /// <summary>
  /// Prose is not covered by plan validation, so the model can still name operators that do not
  /// exist. Flag those ids instead of silently leaving a hallucinated node in the advice.
  /// </summary>
  public static string AnnotateUnknownOperators(string prose, IReadOnlyList<OperatorDescriptor> catalog) {
    var known = catalog.Select(descriptor => descriptor.Type).ToHashSet(StringComparer.Ordinal);
    var namespaces = known.Select(type => type.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
    var unknown = OperatorMention.Matches(prose)
        .Select(match => match.Value)
        .Where(value => !known.Contains(value))
        .Where(value => namespaces.Contains(value.Split('.')[0]))
        .Distinct(StringComparer.Ordinal)
        .ToList();
    if (unknown.Count == 0) {
      return prose;
    }

    return prose + Environment.NewLine + Environment.NewLine +
        $"（提示：{string.Join("、", unknown)} 不在当前算子目录中，已忽略相关建议。 / Not in the operator catalog: {string.Join(", ", unknown)}）";
  }

  /// <summary>
  /// Reads a read-only tool request. The model replies with a fenced block such as
  /// <c>{"tool":"execute_node","nodeId":"otsu"}</c> instead of prose when it needs measurements.
  /// </summary>
  private static bool TryReadToolCall(string proposal, out string? nodeId, out string? problem) {
    nodeId = null;
    problem = null;
    try {
      using var document = JsonDocument.Parse(proposal);
      if (document.RootElement.ValueKind != JsonValueKind.Object ||
          !document.RootElement.TryGetProperty("tool", out var tool) ||
          tool.ValueKind != JsonValueKind.String) {
        return false;
      }

      if (!string.Equals(tool.GetString(), "execute_node", StringComparison.Ordinal)) {
        problem = $"unknown tool '{tool.GetString()}'; the only available tool is execute_node";
        return true;
      }

      if (!document.RootElement.TryGetProperty("nodeId", out var id) || id.ValueKind != JsonValueKind.String ||
          string.IsNullOrWhiteSpace(id.GetString())) {
        problem = "execute_node requires a nodeId";
        return true;
      }

      nodeId = id.GetString();
      return true;
    } catch (JsonException) {
      return false;
    }
  }

  /// <summary>
  /// Reads a sweep request such as
  /// <c>{"tool":"sweep_parameter","nodeId":"otsu","parameter":"threshold","from":60,"to":140,"step":20}</c>.
  /// Returns false when the block is not a sweep at all.
  /// </summary>
  private static bool TryReadSweepCall(string proposal, out AiParameterSweep? sweep, out string? problem) {
    sweep = null;
    problem = null;
    try {
      using var document = JsonDocument.Parse(proposal);
      if (document.RootElement.ValueKind != JsonValueKind.Object ||
          !document.RootElement.TryGetProperty("tool", out var tool) ||
          tool.ValueKind != JsonValueKind.String ||
          !string.Equals(tool.GetString(), "sweep_parameter", StringComparison.Ordinal)) {
        return false;
      }

      var root = document.RootElement;
      if (!root.TryGetProperty("nodeId", out var node) || node.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(node.GetString())) {
        problem = "sweep_parameter requires a nodeId";
        return true;
      }

      if (!root.TryGetProperty("parameter", out var parameter) || parameter.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(parameter.GetString())) {
        problem = "sweep_parameter requires a parameter name";
        return true;
      }

      var values = new List<string>();
      if (root.TryGetProperty("values", out var explicitValues) && explicitValues.ValueKind == JsonValueKind.Array) {
        values.AddRange(explicitValues.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText()));
      } else {
        double from = root.TryGetProperty("from", out var fromValue) && fromValue.TryGetDouble(out double parsedFrom) ? parsedFrom : double.NaN;
        double to = root.TryGetProperty("to", out var toValue) && toValue.TryGetDouble(out double parsedTo) ? parsedTo : double.NaN;
        double step = root.TryGetProperty("step", out var stepValue) && stepValue.TryGetDouble(out double parsedStep) ? parsedStep : double.NaN;
        if (double.IsNaN(from) || double.IsNaN(to) || double.IsNaN(step) || step == 0) {
          problem = "sweep_parameter needs either values[] or from/to/step";
          return true;
        }

        bool ascending = to >= from;
        if ((ascending && step < 0) || (!ascending && step > 0)) {
          step = -step;
        }

        for (double value = from; ascending ? value <= to + 1e-9 : value >= to - 1e-9; value += step) {
          values.Add(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
          if (values.Count > 12) {
            problem = "sweep_parameter is limited to 12 values per call";
            return true;
          }
        }
      }

      if (values.Count == 0) {
        problem = "sweep_parameter produced no values";
        return true;
      }

      if (values.Count > 12) {
        problem = "sweep_parameter is limited to 12 values per call";
        return true;
      }

      sweep = new AiParameterSweep(
          node.GetString()!,
          parameter.GetString()!,
          values,
          root.TryGetProperty("targetNodeId", out var target) && target.ValueKind == JsonValueKind.String ? target.GetString() : null);
      return true;
    } catch (JsonException) {
      return false;
    }
  }

  /// <summary>Runs one node off the caller's thread and renders its outputs compactly.</summary>
  private async Task<string> ExecuteNodeAsync(string nodeId, string? selectedNodeId, CancellationToken cancellationToken) {
    var snapshot = await workflow.GetSnapshotAsync(cancellationToken);
    if (snapshot.Workflow.Nodes.All(node => node.Id != nodeId)) {
      return $"error: node '{nodeId}' does not exist. Available ids: {string.Join(", ", snapshot.Workflow.Nodes.Take(30).Select(node => node.Id))}";
    }

    try {
      var result = await Task.Run(
          () => workflow.ExecuteAsync($"ai-tool-{Guid.NewGuid():N}", nodeId, cancellationToken), cancellationToken);
      var lines = new List<string> {
        result.Success ? $"ok in {result.Timings.Sum(item => item.Duration.TotalMilliseconds):F0} ms" : $"failed: {result.Error}"
      };
      foreach (var output in result.Outputs) {
        lines.Add($"{output.Port} = {output.Summary ?? "(no summary)"}");
      }

      return string.Join("\n", lines);
    } catch (Exception exception) {
      return "error: " + exception.Message;
    }
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

    if (plan.Actions.Count > MaximumActions) {
      throw new InvalidDataException(
          $"AI plan has {plan.Actions.Count} actions but the limit is {MaximumActions}. Prefer one add_pipeline action " +
          "(it accepts a parameters map) or split the proposal into two cards.");
    }

    return NormalizePlan(plan);
  }

  private static readonly Regex ProposalFence = new(
      "```(?:json)?\\s*(\\{.*\\})\\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

  /// <summary>
  /// Splits an assistant reply into its natural-language prose and an optional fenced JSON proposal.
  /// Replies without a fenced block are pure conversation and carry no plan.
  /// </summary>
  public static (string Prose, string? Proposal) SplitProposal(string content) {
    var matches = ProposalFence.Matches(content);
    if (matches.Count == 0) {
      // Tolerate models that answer with a bare plan object and no prose at all.
      string trimmed = content.Trim();
      if (trimmed.StartsWith('{') && trimmed.EndsWith('}') && LooksLikePlan(trimmed)) {
        return ("", trimmed);
      }

      return (trimmed, null);
    }

    var last = matches[^1];
    string prose = (content[..last.Index] + content[(last.Index + last.Length)..]).Trim();
    return (prose, last.Groups[1].Value);
  }

  /// <summary>
  /// Streams when the provider supports it so the UI can show reasoning as it arrives, and
  /// falls back to a buffered call when the endpoint rejects or ignores streaming.
  /// </summary>
  private async Task<AiProviderResponse> SendAsync(
      AiProviderRequest request, IProgress<AiStreamDelta>? progress, CancellationToken cancellationToken) {
    if (progress is not null && provider is IAiStreamingProvider streaming) {
      try {
        return await streaming.SendStreamingAsync(request, delta => progress.Report(delta), cancellationToken);
      } catch (HttpRequestException) {
        // Transport or protocol problem specific to streaming; retry buffered below.
      } catch (NotSupportedException) {
      }
    }

    return await provider.SendAsync(request, cancellationToken);
  }

  /// <summary>
  /// Cheap, deliberately conservative token estimate: CJK text is close to one token per
  /// character while ASCII runs nearer four, so ~2 characters per token keeps prompts inside
  /// the configured window without a tokenizer dependency.
  /// </summary>
  internal static int EstimateTokens(string? text) {
    return string.IsNullOrEmpty(text) ? 0 : (text.Length + 1) / 2;
  }

  private static IEnumerable<AiChatMessage> TrimHistory(IReadOnlyList<AiChatMessage>? conversation, int tokenBudget) {
    if (conversation is not { Count: > 0 }) {
      return [];
    }

    var kept = new List<AiChatMessage>();
    int used = 0;
    for (int index = conversation.Count - 1; index >= 0 && kept.Count < 20; index--) {
      int cost = EstimateTokens(conversation[index].Content) + 8;
      if (used + cost > tokenBudget) {
        break;
      }

      kept.Insert(0, conversation[index]);
      used += cost;
    }

    return kept;
  }

  private static bool LooksLikePlan(string json) {
    try {
      using var document = JsonDocument.Parse(json);
      return document.RootElement.ValueKind == JsonValueKind.Object &&
          (document.RootElement.TryGetProperty("actions", out _) || document.RootElement.TryGetProperty("type", out _));
    } catch (JsonException) {
      return false;
    }
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
      "disconnect_nodes" => 2,
      "connect_nodes" => 3,
      "remove_node" => 4,
      _ => 5
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
        if (action.Parameters is not null) {
          foreach (var (alias, values) in action.Parameters) {
            if (!string.Equals(alias, requestedNodeId, StringComparison.Ordinal) || values is null) {
              continue;
            }

            foreach (var (name, value) in values) {
              expanded.Add(new("set_parameter", NodeId: nodeId, Parameter: name, Value: value));
            }
          }
        }
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
    if (expanded.Count > MaximumActions) {
      throw new InvalidDataException(
          $"Expanded AI pipeline needs {expanded.Count} actions but the limit is {MaximumActions}. " +
          "Drop optional set_parameter actions or split the proposal into two cards.");
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
      if (action.Type is not ("add_node" or "remove_node" or "connect_nodes" or "disconnect_nodes" or "set_parameter")) {
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
      } else if (action.Type == "disconnect_nodes") {
        RequireNode(action.FromNode, nodes);
        RequireNode(action.ToNode, nodes);
        bool exists = workflowDocument.Connections.Any(connection =>
            connection.FromNode == action.FromNode && connection.FromPort == action.FromPort &&
            connection.ToNode == action.ToNode && connection.ToPort == action.ToPort);
        if (!exists) {
          throw new InvalidDataException(
              $"AI disconnect_nodes refers to a connection that does not exist: {action.FromNode}.{action.FromPort} → {action.ToNode}.{action.ToPort}.");
        }

        occupiedInputs.Remove((action.ToNode!, action.ToPort!));
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
      "disconnect_nodes" => new DisconnectEdit(action.FromNode!, action.FromPort!, action.ToNode!, action.ToPort!),
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
