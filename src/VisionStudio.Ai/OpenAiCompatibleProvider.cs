using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VisionStudio.Ai.Abstractions;

namespace VisionStudio.Ai;

public sealed class OpenAiCompatibleProvider : IAiProvider {
  private readonly HttpClient _http;

  public OpenAiCompatibleProvider(AiModelProfile profile, HttpMessageHandler? handler = null) {
    Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    _http = handler is null ? new HttpClient() : new HttpClient(handler);
    _http.BaseAddress = NormalizeBaseAddress(profile.Endpoint);
    _http.Timeout = TimeSpan.FromMinutes(5);
    if (!string.IsNullOrWhiteSpace(profile.ApiKey)) {
      _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
    }
  }

  public string ProviderId => "openai-compatible";
  public AiModelProfile Profile { get; }
  public bool SupportsVision => Profile.SupportsImageInput;

  public async Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default) {
    var messages = new List<object> { new { role = "system", content = request.SystemPrompt } };
    foreach (var message in request.Messages) {
      if (ReferenceEquals(message, request.Messages.LastOrDefault()) && SupportsVision && !string.IsNullOrWhiteSpace(request.PreviewImagePath)) {
        string data = Convert.ToBase64String(await File.ReadAllBytesAsync(request.PreviewImagePath, cancellationToken));
        string media = Path.GetExtension(request.PreviewImagePath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        messages.Add(new { role = message.Role, content = new object[] { new { type = "text", text = message.Content }, new { type = "image_url", image_url = new { url = $"data:{media};base64,{data}" } } } });
      } else {
        messages.Add(new { role = message.Role, content = message.Content });
      }
    }
    var body = new {
      model = Profile.EffectiveModelId,
      messages,
      temperature = request.Temperature,
      max_tokens = Profile.MaximumOutputTokens,
      response_format = request.RequireJson ? new { type = "json_object" } : null
    };
    var stopwatch = Stopwatch.StartNew();
    using var response = await _http.PostAsJsonAsync("chat/completions", body, cancellationToken);
    string raw = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode) {
      throw new HttpRequestException($"OpenAI-compatible endpoint returned {(int)response.StatusCode}: {Limit(raw, 500)}");
    }

    using var json = JsonDocument.Parse(raw);
    string? answer = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    if (string.IsNullOrWhiteSpace(answer)) {
      throw new InvalidDataException("OpenAI-compatible endpoint returned an empty response.");
    }

    return new(answer, Profile.DisplayName, SupportsVision && !string.IsNullOrWhiteSpace(request.PreviewImagePath), stopwatch.Elapsed);
  }

  private static Uri NormalizeBaseAddress(string endpoint) {
    string value = endpoint.TrimEnd('/');
    if (!value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) {
      value += "/v1";
    }

    return new Uri(value + "/", UriKind.Absolute);
  }

  private static string Limit(string value, int maximum) {
    return value.Length <= maximum ? value : value[..maximum];
  }

  public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}

public sealed record AiProviderHandle(IAiProvider Provider, LoadedAiPlugin? Plugin) : IAsyncDisposable {
  public async ValueTask DisposeAsync() {
    await Provider.DisposeAsync();
    if (Plugin is not null) {
      await Plugin.DisposeAsync();
    }
  }
}

public sealed class AiProviderFactory {
  public async Task<AiProviderHandle> CreateAsync(
      AiModelProfile profile,
      VisionStudio.Core.IWorkflowAutomationApi workflow,
      string dataDirectory,
      string hostVersion,
      CancellationToken cancellationToken = default) {
    if (profile.Source == AiModelSource.Remote) {
      return new(new OpenAiCompatibleProvider(profile), null);
    }

    profile = await new LocalAiRuntimeManager().PrepareProfileAsync(profile, cancellationToken: cancellationToken);

    var loader = new AiPluginLoader();
    var roots = AiModelProfileStore.DefaultPluginRoots();
    foreach (string directory in loader.DiscoverDirectories(roots)) {
      string manifestPath = Path.Combine(directory, "plugin.json");
      try {
        var manifest = JsonSerializer.Deserialize<AiPluginManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest?.Id != profile.ProviderPluginId) {
          continue;
        }

        var plugin = await loader.LoadAsync(directory, new(workflow, hostVersion, dataDirectory), cancellationToken);
        try { return new(await plugin.Instance.CreateProviderAsync(profile, cancellationToken), plugin); } catch { await plugin.DisposeAsync(); throw; }
      } catch (JsonException) { }
    }
    string? runtime = new LocalAiRuntimeManager().FindLlamaServer(profile.LlamaServerPath);
    string runtimeHint = runtime is null
        ? "llama-server 运行时也未找到。"
        : $"已找到 llama-server：'{runtime}'，但它只是推理运行时，不是 Vision Studio Provider 插件。";
    throw new InvalidOperationException(
        $"未找到本地 AI 连接插件 '{profile.ProviderPluginId}'。{runtimeHint} " +
        $"请把 VisionStudio.Ai.LlamaCpp 插件包安装到 '{AiSoftwarePaths.PluginsDirectory}'。" +
        $"\nLocal AI provider plugin was not found. Searched: {string.Join("; ", roots)}");
  }
}
