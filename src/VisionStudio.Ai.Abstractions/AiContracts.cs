using System.Text.Json.Serialization;
using VisionStudio.Core;

namespace VisionStudio.Ai.Abstractions;

public static class AiContract {
  public const int Version = 2;
}

/// <summary>
/// Reasoning effort sent as <c>reasoning_effort</c>. <see cref="Auto"/> sends nothing and lets
/// the endpoint decide; <see cref="Off"/> disables thinking through <c>thinking.type=disabled</c>,
/// because OpenAI-compatible gateways reject the literal <c>reasoning_effort=off</c>.
/// </summary>
public enum AiReasoningEffort { Auto, Off, Low, High, Max }

/// <summary>
/// A remote OpenAI-compatible model endpoint. Vision Studio only supports remote models:
/// there is no local runtime, model file, or provider plugin to configure.
/// <para>
/// <see cref="MaxInputTokens"/> is the context window used to trim conversation history and the
/// operator catalog before a request; <see cref="MaximumOutputTokens"/> becomes <c>max_tokens</c>.
/// </para>
/// </summary>
public sealed record AiModelProfile(
    string Id,
    string DisplayName,
    string Endpoint = "https://api.openai.com/v1",
    string? ModelId = null,
    string? ApiKey = null,
    bool SupportsImageInput = false,
    int MaximumOutputTokens = 4096,
    int MaxInputTokens = 65536,
    AiReasoningEffort ReasoningEffort = AiReasoningEffort.Auto) {
  [JsonIgnore]
  public string EffectiveModelId => string.IsNullOrWhiteSpace(ModelId) ? Id : ModelId;

  /// <summary>Wire value for <c>reasoning_effort</c>, or null when nothing should be sent.</summary>
  [JsonIgnore]
  public string? ReasoningEffortParameter => ReasoningEffort switch {
    AiReasoningEffort.Low => "low",
    AiReasoningEffort.High => "high",
    AiReasoningEffort.Max => "max",
    _ => null
  };

  public override string ToString() {
    return DisplayName;
  }
}

/// <summary>
/// One turn of the assistant conversation. <paramref name="Kind"/> separates what the model
/// actually said from UI-only notices and tool output, so those never leak back into a prompt.
/// </summary>
public sealed record AiChatMessage(string Role, string Content, string Kind = AiChatMessageKinds.Chat);

public static class AiChatMessageKinds {
  public const string Chat = "chat";
  public const string Error = "error";
  public const string Execution = "execution";
  public const string Sweep = "sweep";
}

public sealed record AiProviderRequest(
    string SystemPrompt,
    IReadOnlyList<AiChatMessage> Messages,
    string? PreviewImagePath = null,
    bool RequireJson = false,
    double Temperature = 0.3);

public sealed record AiProviderResponse(string Content, string Model, bool VisionUsed, TimeSpan Duration);

/// <summary>One incremental piece of a streamed reply: either reasoning or answer text.</summary>
public sealed record AiStreamDelta(bool IsReasoning, string Text);

/// <summary>
/// Optional capability: providers that can stream report deltas while the answer is generated.
/// Callers fall back to <see cref="IAiProvider.SendAsync"/> when a provider does not implement it.
/// </summary>
public interface IAiStreamingProvider {
  Task<AiProviderResponse> SendStreamingAsync(
      AiProviderRequest request, Action<AiStreamDelta> onDelta, CancellationToken cancellationToken = default);
}

/// <summary>One parameter sweep: run <paramref name="NodeId"/> with <paramref name="Parameter"/>
/// set to each value and report the outputs of <paramref name="TargetNodeId"/>.</summary>
public sealed record AiParameterSweep(
    string NodeId,
    string Parameter,
    IReadOnlyList<string> Values,
    string? TargetNodeId = null);

/// <summary>One swept value with the metrics it produced on the measured node.</summary>
public sealed record AiSweepPoint(string Value, IReadOnlyDictionary<string, double> Metrics, double Milliseconds);

/// <summary>A complete sweep, kept structured so the UI can draw it instead of re-parsing text.</summary>
public sealed record AiSweepSeries(
    string NodeId,
    string Parameter,
    string TargetNodeId,
    IReadOnlyList<AiSweepPoint> Points,
    string MetricName);

/// <summary>
/// Read-only workflow probes the assistant may call. Implemented by the desktop shell, which owns
/// the operator registry and project root; the AI layer itself never touches OpenCV.
/// </summary>
public interface IAiWorkflowTools {
  Task<string> ExecuteNodeAsync(string nodeId, CancellationToken cancellationToken = default);
  Task<string> SweepParameterAsync(AiParameterSweep sweep, CancellationToken cancellationToken = default);
}

public interface IAiProvider : IAsyncDisposable {
  string ProviderId { get; }
  AiModelProfile Profile { get; }
  bool SupportsVision { get; }
  Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default);
}

public sealed record AiAssistantRequest(
    string Text,
    string? SelectedNodeId = null,
    string? PreviewImagePath = null,
    IReadOnlyList<AiChatMessage>? Conversation = null);

/// <summary>
/// Assistant reply. <see cref="Message"/> is natural language and is always shown to the user.
/// <see cref="Plan"/> is an optional proposal rendered as a review card; nothing is applied
/// to the canvas until the user explicitly accepts it.
/// </summary>
public sealed record AiAssistantResponse(
    string Message,
    AiWorkflowPlan? Plan = null,
    WorkflowMutationResult? Mutation = null,
    WorkflowAutomationRunResult? Execution = null);

public sealed record AiWorkflowPlan(string Summary, IReadOnlyList<AiGraphAction> Actions, string? TargetNodeId = null);

public sealed record AiGraphAction(
    string Type,
    string? NodeId = null,
    string? OperatorId = null,
    string? FromNode = null,
    string? FromPort = null,
    string? ToNode = null,
    string? ToPort = null,
    string? Parameter = null,
    object? Value = null,
    IReadOnlyList<string>? OperatorIds = null,
    IReadOnlyList<string>? NodeIds = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? Parameters = null);
