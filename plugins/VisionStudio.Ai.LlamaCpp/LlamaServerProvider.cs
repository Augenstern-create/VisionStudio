using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using VisionStudio.Ai.Abstractions;

namespace VisionStudio.Ai.LlamaCpp;

public sealed class LlamaServerProvider : IAiProvider {
  private readonly HttpClient _http;
  private readonly SemaphoreSlim _startGate = new(1, 1);
  private Process? _ownedProcess;
  private bool _disposed;

  public LlamaServerProvider(AiModelProfile profile, HttpMessageHandler? handler = null) {
    Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    if (!new Uri(profile.Endpoint, UriKind.Absolute).IsLoopback) {
      throw new InvalidOperationException("The local AI provider endpoint must use a loopback address.");
    }

    _http = handler is null ? new HttpClient() : new HttpClient(handler);
    _http.BaseAddress = new Uri(profile.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
    _http.Timeout = TimeSpan.FromMinutes(5);
  }

  public string ProviderId => "llama.cpp.server";
  public AiModelProfile Profile { get; }
  public bool SupportsVision => Profile.HasVisionProjector;

  public async Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    await EnsureServerAsync(cancellationToken);
    var messages = new List<object> { new { role = "system", content = request.SystemPrompt } };
    foreach (var message in request.Messages) {
      if (message == request.Messages.LastOrDefault() && SupportsVision && !string.IsNullOrWhiteSpace(request.PreviewImagePath)) {
        string data = Convert.ToBase64String(await File.ReadAllBytesAsync(request.PreviewImagePath, cancellationToken));
        string media = Path.GetExtension(request.PreviewImagePath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        messages.Add(new { role = message.Role, content = new object[] { new { type = "text", text = message.Content }, new { type = "image_url", image_url = new { url = $"data:{media};base64,{data}" } } } });
      } else {
        messages.Add(new { role = message.Role, content = message.Content });
      }
    }
    var body = new { model = Profile.EffectiveModelId, messages, temperature = request.Temperature, max_tokens = Math.Max(Profile.MaximumOutputTokens, 2048), response_format = request.RequireJson ? new { type = "json_object" } : null };
    var stopwatch = Stopwatch.StartNew();
    using var response = await _http.PostAsJsonAsync("v1/chat/completions", body, cancellationToken);
    string content = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode) {
      throw new HttpRequestException($"llama-server returned {(int)response.StatusCode}: {Limit(content, 500)}");
    }

    using var json = JsonDocument.Parse(content);
    string? answer = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    if (string.IsNullOrWhiteSpace(answer)) {
      throw new InvalidDataException("llama-server returned an empty assistant message.");
    }

    return new(answer, Profile.DisplayName, SupportsVision && !string.IsNullOrWhiteSpace(request.PreviewImagePath), stopwatch.Elapsed);
  }

  private async Task EnsureServerAsync(CancellationToken cancellationToken) {
    if (await IsHealthyAsync(cancellationToken)) {
      return;
    }

    await _startGate.WaitAsync(cancellationToken);
    try {
      if (await IsHealthyAsync(cancellationToken)) {
        return;
      }

      ValidateLocalFiles();
      var endpoint = new Uri(Profile.Endpoint);
      if (!endpoint.IsLoopback) {
        throw new InvalidOperationException("Managed llama-server must use a loopback endpoint.");
      }

      var start = new ProcessStartInfo {
        FileName = Path.GetFullPath(Profile.LlamaServerPath),
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(Profile.LlamaServerPath))!,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = false,
        RedirectStandardError = false
      };
      Add(start, "--model", Path.GetFullPath(Profile.ModelPath), "--host", endpoint.Host, "--port", endpoint.Port.ToString(), "--ctx-size", Math.Clamp(Math.Max(Profile.ContextSize, 8192), 8192, 32768).ToString(), "--n-gpu-layers", Profile.GpuLayers.ToString(), "--jinja");
      if (Profile.HasVisionProjector) {
        Add(start, "--mmproj", Path.GetFullPath(Profile.MmprojPath!));
      } else {
        Add(start, "--no-mmproj");
      }

      _ownedProcess = Process.Start(start) ?? throw new InvalidOperationException("Could not start llama-server.");
      var deadline = DateTime.UtcNow.AddMinutes(3);
      while (DateTime.UtcNow < deadline) {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ownedProcess.HasExited) {
          throw new InvalidOperationException($"llama-server exited with code {_ownedProcess.ExitCode}.");
        }

        if (await IsHealthyAsync(cancellationToken)) {
          return;
        }

        await Task.Delay(500, cancellationToken);
      }
      throw new TimeoutException("llama-server did not become healthy within three minutes.");
    } catch {
      StopOwnedProcess();
      throw;
    } finally { _startGate.Release(); }
  }

  private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken) {
    try {
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(TimeSpan.FromSeconds(2));
      using var response = await _http.GetAsync("health", timeout.Token);
      return response.IsSuccessStatusCode;
    } catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { return false; }
  }

  private void ValidateLocalFiles() {
    if (!File.Exists(Profile.LlamaServerPath)) {
      throw new FileNotFoundException("llama-server executable does not exist.", Profile.LlamaServerPath);
    }

    if (!File.Exists(Profile.ModelPath)) {
      throw new FileNotFoundException("GGUF model does not exist.", Profile.ModelPath);
    }

    if (!string.IsNullOrWhiteSpace(Profile.MmprojPath) && !File.Exists(Profile.MmprojPath)) {
      throw new FileNotFoundException("Multimodal projector does not exist.", Profile.MmprojPath);
    }
  }

  private static void Add(ProcessStartInfo start, params string[] arguments) {
    foreach (string argument in arguments) {
      start.ArgumentList.Add(argument);
    }
  }

  private static string Limit(string value, int length) {
    return value.Length <= length ? value : value[..length];
  }

  public ValueTask DisposeAsync() {
    if (_disposed) {
      return ValueTask.CompletedTask;
    }

    _disposed = true;
    StopOwnedProcess();
    _http.Dispose();
    _startGate.Dispose();
    return ValueTask.CompletedTask;
  }

  private void StopOwnedProcess() {
    if (_ownedProcess is null) {
      return;
    }

    try { if (!_ownedProcess.HasExited) { _ownedProcess.Kill(entireProcessTree: true); } } catch (InvalidOperationException) { } finally { _ownedProcess.Dispose(); _ownedProcess = null; }
  }
}
