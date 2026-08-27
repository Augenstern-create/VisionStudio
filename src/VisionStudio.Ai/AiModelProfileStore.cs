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
  public static string LocalProfilesFile => Path.Combine(ConfigDirectory, "ai-local-models.json");
  public static string RemoteProfilesFile => Path.Combine(ConfigDirectory, "ai-remote-models.json");
  public static string ContextsFile => Path.Combine(ConfigDirectory, "ai-contexts.json");
  public static string StandardModelsFile {
    get {
      string userFile = Path.Combine(ConfigDirectory, "standard-models.json");
      string bundledFile = Path.Combine(AppContext.BaseDirectory, "config", "standard-models.json");
      return File.Exists(userFile) || !File.Exists(bundledFile) ? userFile : bundledFile;
    }
  }
  public static string ModelsDirectory => Path.Combine(DataDirectory, "models");
  public static string RuntimeDirectory => Path.Combine(DataDirectory, "runtime", "llama.cpp");
  public static string PluginsDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");
  public static string UserPluginsDirectory => Path.Combine(DataDirectory, "plugins");

  private static string DataDirectory {
    get {
      if (OperatingSystem.IsWindows()) {
        return AppContext.BaseDirectory;
      }
      string root = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
      return Path.Combine(root, "visionstudio");
    }
  }
}

public sealed record StandardAiModelDefinition(
    string Id, string DisplayName, string ModelFileName, string? MmprojFileName,
    string ModelId, string DownloadUrl, string? MmprojDownloadUrl = null, string Description = "",
    long? ModelSizeBytes = null, string? ModelSha256 = null,
    long? MmprojSizeBytes = null, string? MmprojSha256 = null,
    string License = "", string SourceUrl = "") {
  public override string ToString() {
    return DisplayName;
  }
}

public sealed record EncryptedRemoteAiModelProfile(string Id, string EncryptedPayload);
internal sealed record RemoteAiModelParameters(
    string DisplayName, string ModelId, string Endpoint, string? ApiKey,
    bool SupportsImageInput, int MaximumOutputTokens = 512);

/// <summary>Persists local and remote profiles in deliberately separate files and formats.</summary>
public sealed class AiModelProfileStore {
  private readonly LocalAiModelProfileStore _local;
  private readonly RemoteAiModelProfileStore _remote;

  public AiModelProfileStore(string? localFilePath = null, string? remoteFilePath = null,
      string? standardModelsPath = null, ISecretProtector? secretProtector = null) {
    LocalFilePath = Path.GetFullPath(localFilePath ?? AiSoftwarePaths.LocalProfilesFile);
    RemoteFilePath = Path.GetFullPath(remoteFilePath ?? AiSoftwarePaths.RemoteProfilesFile);
    StandardModelsPath = Path.GetFullPath(standardModelsPath ?? AiSoftwarePaths.StandardModelsFile);
    _local = new(LocalFilePath);
    _remote = new(RemoteFilePath, secretProtector ?? SecretProtector.CreateDefault());
  }

  public string LocalFilePath { get; }
  public string RemoteFilePath { get; }
  public string StandardModelsPath { get; }

  public async Task<IReadOnlyList<AiModelProfile>> LoadAsync(CancellationToken cancellationToken = default) {
    return [.. (await _local.LoadAsync(cancellationToken)), .. await _remote.LoadAsync(cancellationToken)];
  }

  public Task<IReadOnlyList<AiModelProfile>> LoadLocalAsync(CancellationToken cancellationToken = default) {
    return _local.LoadAsync(cancellationToken);
  }

  public Task<IReadOnlyList<AiModelProfile>> LoadRemoteAsync(CancellationToken cancellationToken = default) {
    return _remote.LoadAsync(cancellationToken);
  }

  public async Task SaveAsync(IReadOnlyList<AiModelProfile> profiles, CancellationToken cancellationToken = default) {
    if (profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Count) {
      throw new InvalidDataException("AI model profile ids must be unique.");
    }

    await _local.SaveAsync([.. profiles.Where(profile => profile.Source != AiModelSource.Remote)], cancellationToken);
    await _remote.SaveAsync([.. profiles.Where(profile => profile.Source == AiModelSource.Remote)], cancellationToken);
  }

  public async Task<IReadOnlyList<StandardAiModelDefinition>> LoadStandardModelsAsync(CancellationToken cancellationToken = default) {
    if (!File.Exists(StandardModelsPath)) {
      return [];
    }

    var definitions = JsonSerializer.Deserialize<List<StandardAiModelDefinition>>(
        await File.ReadAllTextAsync(StandardModelsPath, cancellationToken), WorkflowJson.Options) ?? [];
    foreach (var definition in definitions) {
      ValidateStandardModel(definition);
    }
    return definitions;
  }

  public static AiModelProfile CreateStandardProfile(StandardAiModelDefinition definition, string? llamaServerPath = null) {
    return new(
      definition.Id, definition.DisplayName, Path.Combine(AiSoftwarePaths.ModelsDirectory, definition.ModelFileName),
      string.IsNullOrWhiteSpace(definition.MmprojFileName) ? null : Path.Combine(AiSoftwarePaths.ModelsDirectory, definition.MmprojFileName),
      llamaServerPath ?? "", Source: AiModelSource.StandardLocal, ModelId: definition.ModelId,
      SupportsImageInput: !string.IsNullOrWhiteSpace(definition.MmprojFileName), StandardModelId: definition.Id,
      DownloadUrl: definition.DownloadUrl);
  }

  public static IReadOnlyList<string> DefaultPluginRoots() {
    // AppContext itself is important for development/test hosts: project references
    // copy plugin.json and the provider assembly next to the executable.
    var roots = new List<string> {
      AiSoftwarePaths.UserPluginsDirectory,
      AiSoftwarePaths.PluginsDirectory,
      AppContext.BaseDirectory
    };
    string? configured = Environment.GetEnvironmentVariable("VISIONSTUDIO_AI_PLUGIN_PATH");
    if (!string.IsNullOrWhiteSpace(configured)) {
      roots.InsertRange(0, configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    // A normal App build deliberately does not reference optional providers. When
    // running from a source checkout, locate already-built plugin outputs instead
    // of requiring developers to copy them into every bin directory by hand.
    string? workspace = FindWorkspaceRoot(AppContext.BaseDirectory);
    if (workspace is not null) {
      foreach (string? configuration in new[] { "Debug", "Release" }) {
        roots.Add(Path.Combine(workspace, "plugins", "VisionStudio.Ai.LlamaCpp", "bin", configuration, "net9.0"));
      }
    }
    return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
  }

  private static string? FindWorkspaceRoot(string start) {
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent) {
      if (File.Exists(Path.Combine(directory.FullName, "VisionStudio.sln"))) {
        return directory.FullName;
      }
    }

    return null;
  }

  private static void ValidateStandardModel(StandardAiModelDefinition definition) {
    if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.DisplayName)
        || string.IsNullOrWhiteSpace(definition.ModelId)) {
      throw new InvalidDataException("Standard AI model id, display name and model id are required.");
    }
    ValidateDownload(
        definition.ModelFileName, definition.DownloadUrl,
        definition.ModelSizeBytes, definition.ModelSha256, "model");
    if (!string.IsNullOrWhiteSpace(definition.MmprojFileName)) {
      ValidateDownload(
          definition.MmprojFileName, definition.MmprojDownloadUrl,
          definition.MmprojSizeBytes, definition.MmprojSha256, "vision projection model");
    }
    if (string.IsNullOrWhiteSpace(definition.License) ||
        !Uri.TryCreate(definition.SourceUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps) {
      throw new InvalidDataException($"Standard AI model '{definition.Id}' must declare its license and HTTPS source URL.");
    }
  }

  private static void ValidateDownload(
      string? fileName, string? downloadUrl, long? sizeBytes, string? sha256, string artifact) {
    if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName) {
      throw new InvalidDataException($"Standard AI {artifact} file name must be a plain file name.");
    }
    if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) {
      throw new InvalidDataException($"Standard AI {artifact} must use an HTTPS download URL.");
    }
    if (sizeBytes is null or <= 0 || sha256?.Length != 64 || !sha256.All(Uri.IsHexDigit)) {
      throw new InvalidDataException($"Standard AI {artifact} must declare a positive size and SHA-256 checksum.");
    }
  }
}

public sealed class LocalAiModelProfileStore(string filePath) {
  public string FilePath { get; } = Path.GetFullPath(filePath);
  public async Task<IReadOnlyList<AiModelProfile>> LoadAsync(CancellationToken cancellationToken = default) {
    if (!File.Exists(FilePath)) {
      return [];
    }

    var profiles = JsonSerializer.Deserialize<List<AiModelProfile>>(
        await File.ReadAllTextAsync(FilePath, cancellationToken), WorkflowJson.Options) ?? [];
    return [.. profiles.Where(profile => profile.Source != AiModelSource.Remote && !string.IsNullOrWhiteSpace(profile.Id))];
  }
  public Task SaveAsync(IReadOnlyList<AiModelProfile> profiles, CancellationToken cancellationToken = default) {
    return AtomicJsonFile.WriteAsync(FilePath, profiles.Select(profile => profile with { ApiKey = null }).ToList(), cancellationToken);
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
      return new AiModelProfile(profile.Id, parameters.DisplayName, "", null, "",
          MaximumOutputTokens: parameters.MaximumOutputTokens, Endpoint: parameters.Endpoint,
          Source: AiModelSource.Remote, ModelId: parameters.ModelId, ApiKey: parameters.ApiKey,
          SupportsImageInput: parameters.SupportsImageInput, ProviderPluginId: "");
    })];
  }
  public Task SaveAsync(IReadOnlyList<AiModelProfile> profiles, CancellationToken cancellationToken = default) {
    return AtomicJsonFile.WriteAsync(FilePath, profiles.Select(profile => {
      var parameters = new RemoteAiModelParameters(profile.DisplayName, profile.EffectiveModelId,
            profile.Endpoint, profile.ApiKey, profile.SupportsImageInput, profile.MaximumOutputTokens);
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
