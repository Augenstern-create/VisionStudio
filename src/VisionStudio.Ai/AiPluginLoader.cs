using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using VisionStudio.Ai.Abstractions;

namespace VisionStudio.Ai;

public sealed record LoadedAiPlugin(string Directory, AiPluginManifest Manifest, IAiAssistantPlugin Instance, AssemblyLoadContext LoadContext) : IAsyncDisposable {
  public async ValueTask DisposeAsync() {
    await Instance.DisposeAsync();
    LoadContext.Unload();
  }
}

public sealed class AiPluginLoader {
  public IReadOnlyList<string> DiscoverDirectories(IEnumerable<string> roots) {
    return [.. roots.Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFiles(root, "plugin.json", SearchOption.AllDirectories))
        .Select(Path.GetDirectoryName).Where(path => path is not null).Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
  }

  public async Task<LoadedAiPlugin> LoadAsync(string pluginDirectory, AiPluginContext context, CancellationToken cancellationToken = default) {
    string root = Path.GetFullPath(pluginDirectory);
    string manifestPath = ResolveInside(root, "plugin.json");
    var manifest = JsonSerializer.Deserialize<AiPluginManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), JsonOptions)
        ?? throw new InvalidDataException("AI plugin manifest is empty.");
    Validate(manifest);
    if (!Version.TryParse(context.HostVersion, out var hostVersion) ||
        !Version.TryParse(manifest.MinimumHostVersion, out var minimumHostVersion) ||
        hostVersion < minimumHostVersion) {
      throw new NotSupportedException($"AI plugin '{manifest.Id}' requires Vision Studio {manifest.MinimumHostVersion} or later.");
    }

    string assemblyPath = ResolveInside(root, manifest.EntryAssembly);
    if (!File.Exists(assemblyPath)) {
      throw new FileNotFoundException("AI plugin entry assembly does not exist.", assemblyPath);
    }

    var loadContext = new PluginLoadContext(assemblyPath);
    try {
      var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
      var type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
      if (Activator.CreateInstance(type) is not IAiAssistantPlugin plugin) {
        throw new InvalidDataException($"Plugin entry type '{manifest.EntryType}' does not implement {nameof(IAiAssistantPlugin)}.");
      }

      if (!string.Equals(plugin.Manifest.Id, manifest.Id, StringComparison.Ordinal) || plugin.Manifest.SchemaVersion != manifest.SchemaVersion) {
        throw new InvalidDataException("Plugin manifest does not match the entry implementation.");
      }

      await plugin.InitializeAsync(context, cancellationToken);
      return new(root, manifest, plugin, loadContext);
    } catch { loadContext.Unload(); throw; }
  }

  private static void Validate(AiPluginManifest manifest) {
    if (manifest.SchemaVersion != AiContract.Version) {
      throw new NotSupportedException($"AI plugin contract {manifest.SchemaVersion} is not supported.");
    }

    if (string.IsNullOrWhiteSpace(manifest.Id) || !manifest.Id.All(character => char.IsLetterOrDigit(character) || character is '.' or '-')) {
      throw new InvalidDataException("AI plugin id is invalid.");
    }

    ArgumentException.ThrowIfNullOrWhiteSpace(manifest.EntryAssembly);
    ArgumentException.ThrowIfNullOrWhiteSpace(manifest.EntryType);
    if (!Version.TryParse(manifest.Version, out _)) {
      throw new InvalidDataException("AI plugin version is invalid.");
    }
  }

  private static string ResolveInside(string root, string relative) {
    if (Path.IsPathRooted(relative)) {
      throw new InvalidDataException("AI plugin paths must be relative.");
    }

    string path = Path.GetFullPath(Path.Combine(root, relative));
    string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidDataException("AI plugin path escapes the plugin directory.");
    }

    return path;
  }

  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

  private sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true) {
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);
    protected override Assembly? Load(AssemblyName assemblyName) {
      if (assemblyName.Name is "VisionStudio.Core" or "VisionStudio.Ai.Abstractions") {
        return null;
      }

      string? path = _resolver.ResolveAssemblyToPath(assemblyName);
      return path is null ? null : LoadFromAssemblyPath(path);
    }
  }
}
