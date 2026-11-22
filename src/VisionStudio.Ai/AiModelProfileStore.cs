using System.Text.Json;
using VisionStudio.Ai.Abstractions;
using VisionStudio.Core;

namespace VisionStudio.Ai;

public static class AiSoftwarePaths {
  public static string ConfigDirectory {
    get {
      string? testDirectory = Environment.GetEnvironmentVariable("VISIONSTUDIO_TEST_CONFIG_DIRECTORY");
      if (Environment.GetEnvironmentVariable("VISIONSTUDIO_TEST_MODE") == "1" && !string.IsNullOrWhiteSpace(testDirectory)) {
        return Path.GetFullPath(testDirectory);
      }
      if (OperatingSystem.IsWindows()) {
        return Path.Combine(AppContext.BaseDirectory, "config");
      }
      string root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
      return Path.Combine(root, "visionstudio");
    }
  }
  public static string RemoteProfilesFile => Path.Combine(ConfigDirectory, "ai-remote-models.json");
  public static string ContextsFile => Path.Combine(ConfigDirectory, "ai-contexts.json");
}

public sealed record EncryptedRemoteAiModelProfile(string Id, string EncryptedPayload);
internal sealed record RemoteAiModelParameters(
    string DisplayName, string ModelId, string Endpoint, string? ApiKey,
    bool SupportsImageInput, int MaximumOutputTokens = 4096,
    int MaxInputTokens = 65536, AiReasoningEffort ReasoningEffort = AiReasoningEffort.Auto);

/// <summary>Persists remote OpenAI-compatible profiles with their secrets encrypted.</summary>
public sealed class AiModelProfileStore {
  private readonly RemoteAiModelProfileStore _remote;

  public AiModelProfileStore(string? remoteFilePath = null, ISecretProtector? secretProtector = null) {
    RemoteFilePath = Path.GetFullPath(remoteFilePath ?? AiSoftwarePaths.RemoteProfilesFile);
    _remote = new(RemoteFilePath, secretProtector ?? SecretProtector.CreateDefault());
  }

  public string RemoteFilePath { get; }

  public Task<IReadOnlyList<AiModelProfile>> LoadAsync(CancellationToken cancellationToken = default) {
    return _remote.LoadAsync(cancellationToken);
  }

  public Task<IReadOnlyList<AiModelProfile>> LoadRemoteAsync(CancellationToken cancellationToken = default) {
    return _remote.LoadAsync(cancellationToken);
  }

  public async Task SaveAsync(IReadOnlyList<AiModelProfile> profiles, CancellationToken cancellationToken = default) {
    if (profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Count) {
      throw new InvalidDataException("AI model profile ids must be unique.");
    }

    await _remote.SaveAsync(profiles, cancellationToken);
  }
}

public sealed class RemoteAiModelProfileStore(string filePath, ISecretProtector secretProtector) {
  public string FilePath { get; } = Path.GetFullPath(filePath);
  public async Task<IReadOnlyList<AiModelProfile>> LoadAsync(CancellationToken cancellationToken = default) {
    if (!File.Exists(FilePath)) {
      return [];
    }

    var stored = JsonSerializer.Deserialize<List<EncryptedRemoteAiModelProfile>>(
        await File.ReadAllTextAsync(FilePath, cancellationToken), WorkflowJson.Options) ?? [];
    return [.. stored.Select(profile => {
      var parameters = JsonSerializer.Deserialize<RemoteAiModelParameters>(
          secretProtector.Unprotect(profile.EncryptedPayload), WorkflowJson.Options)
          ?? throw new InvalidDataException($"Remote AI profile '{profile.Id}' has no encrypted parameters.");
      return new AiModelProfile(profile.Id, parameters.DisplayName, parameters.Endpoint,
          parameters.ModelId, parameters.ApiKey, parameters.SupportsImageInput,
          parameters.MaximumOutputTokens, parameters.MaxInputTokens, parameters.ReasoningEffort);
    })];
  }
  public Task SaveAsync(IReadOnlyList<AiModelProfile> profiles, CancellationToken cancellationToken = default) {
    return AtomicJsonFile.WriteAsync(FilePath, profiles.Select(profile => {
      var parameters = new RemoteAiModelParameters(profile.DisplayName, profile.EffectiveModelId,
            profile.Endpoint, profile.ApiKey, profile.SupportsImageInput, profile.MaximumOutputTokens,
            profile.MaxInputTokens, profile.ReasoningEffort);
      return new EncryptedRemoteAiModelProfile(profile.Id,
            secretProtector.Protect(JsonSerializer.Serialize(parameters, WorkflowJson.Options)));
    }).ToList(), cancellationToken);
  }
}

internal static class AtomicJsonFile {
  public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken) {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string temporary = path + ".tmp";
    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, WorkflowJson.Options), cancellationToken);
    File.Move(temporary, path, overwrite: true);
  }
}

public sealed class AiConversationContextStore(string? filePath = null) {
  public string FilePath { get; } = Path.GetFullPath(filePath ?? AiSoftwarePaths.ContextsFile);
  public async Task<IReadOnlyDictionary<string, IReadOnlyList<AiChatMessage>>> LoadAsync(CancellationToken cancellationToken = default) {
    if (!File.Exists(FilePath)) {
      return new Dictionary<string, IReadOnlyList<AiChatMessage>>();
    }

    var values = JsonSerializer.Deserialize<Dictionary<string, List<AiChatMessage>>>(
        await File.ReadAllTextAsync(FilePath, cancellationToken), WorkflowJson.Options) ?? [];
    return values.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<AiChatMessage>)pair.Value, StringComparer.Ordinal);
  }
  public Task SaveAsync(IReadOnlyDictionary<string, IReadOnlyList<AiChatMessage>> contexts, CancellationToken cancellationToken = default) {
    return AtomicJsonFile.WriteAsync(FilePath,
          contexts.ToDictionary(pair => pair.Key, pair => pair.Value.TakeLast(40).ToList(), StringComparer.Ordinal), cancellationToken);
  }
}
