using VisionStudio.Ai.Abstractions;

namespace VisionStudio.Ai.LlamaCpp;

public sealed class LlamaCppAssistantPlugin : IAiAssistantPlugin {
  private AiPluginContext? _context;

  public AiPluginManifest Manifest { get; } = new(
      1, "visionstudio.ai.llamacpp", "Vision Studio Local AI - llama.cpp", "2.0.0",
      "VisionStudio.Ai.LlamaCpp.dll", typeof(LlamaCppAssistantPlugin).FullName!, "2.0.0",
      ["chat", "operator-search", "graph-actions", "vision-when-mmproj-present"]);

  public Task InitializeAsync(AiPluginContext context, CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    _context = context ?? throw new ArgumentNullException(nameof(context));
    Directory.CreateDirectory(context.DataDirectory);
    return Task.CompletedTask;
  }

  public Task<IAiProvider> CreateProviderAsync(AiModelProfile profile, CancellationToken cancellationToken = default) {
    if (_context is null) {
      throw new InvalidOperationException("AI plugin is not initialized.");
    }

    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult<IAiProvider>(new LlamaServerProvider(profile));
  }

  public ValueTask DisposeAsync() {
    _context = null;
    return ValueTask.CompletedTask;
  }
}
