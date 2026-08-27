using System.Text.Json.Serialization;
using VisionStudio.Core;

namespace VisionStudio.Ai.Abstractions;

public static class AiContract {
  public const int Version = 1;
}

public sealed record AiPluginManifest(
    int SchemaVersion,
    string Id,
    string Name,
    string Version,
    string EntryAssembly,
    string EntryType,
    string MinimumHostVersion,
    IReadOnlyList<string> Capabilities);

public sealed record AiModelProfile(
    string Id,
    string DisplayName,
    string ModelPath,
    string? MmprojPath,
    string LlamaServerPath,
    int ContextSize = 8192,
    int GpuLayers = -1,
    string Endpoint = "http://127.0.0.1:11435",
    int MaximumOutputTokens = 512,
    AiModelSource Source = AiModelSource.Local,
    string? ModelId = null,
    string? ApiKey = null,
    bool SupportsImageInput = false,
    string ProviderPluginId = "visionstudio.ai.llamacpp",
    string? StandardModelId = null,
    string? DownloadUrl = null) {
  [JsonIgnore]
  public bool HasVisionProjector => !string.IsNullOrWhiteSpace(MmprojPath) && File.Exists(MmprojPath);
  [JsonIgnore]
  public string EffectiveModelId => string.IsNullOrWhiteSpace(ModelId) ? Id : ModelId;
  public override string ToString() {
    return DisplayName;
  }
}

public enum AiModelSource { Local, Remote, StandardLocal }

public sealed record AiChatMessage(string Role, string Content);

public sealed record AiProviderRequest(
    string SystemPrompt,
    IReadOnlyList<AiChatMessage> Messages,
    string? PreviewImagePath = null,
    bool RequireJson = true,
    double Temperature = 0.1);

public sealed record AiProviderResponse(string Content, string Model, bool VisionUsed, TimeSpan Duration);

public interface IAiProvider : IAsyncDisposable {
  string ProviderId { get; }
  AiModelProfile Profile { get; }
  bool SupportsVision { get; }
  Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default);
}

public sealed record AiPluginContext(IWorkflowAutomationApi Workflow, string HostVersion, string DataDirectory);

public interface IAiAssistantPlugin : IAsyncDisposable {
  AiPluginManifest Manifest { get; }
  Task InitializeAsync(AiPluginContext context, CancellationToken cancellationToken = default);
  Task<IAiProvider> CreateProviderAsync(AiModelProfile profile, CancellationToken cancellationToken = default);
}

public enum AiAssistantMode { Suggest, Automatic }

public sealed record AiAssistantRequest(
    string Text,
    string? SelectedNodeId = null,
    string? PreviewImagePath = null,
    AiAssistantMode Mode = AiAssistantMode.Suggest,
    IReadOnlyList<AiChatMessage>? Conversation = null);

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
    IReadOnlyList<string>? NodeIds = null);
