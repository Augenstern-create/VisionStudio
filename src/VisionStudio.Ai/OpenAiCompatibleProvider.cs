using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text;
using System.Text.Json;
using VisionStudio.Ai.Abstractions;

namespace VisionStudio.Ai;

public sealed class OpenAiCompatibleProvider : IAiProvider, IAiStreamingProvider {
  private readonly HttpClient _http;

  public OpenAiCompatibleProvider(AiModelProfile profile, HttpMessageHandler? handler = null) {
    Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    _http = handler is null ? new HttpClient() : new HttpClient(handler);
    ChatUri = ResolveChatCompletionsUri(profile.Endpoint);
    _http.Timeout = TimeSpan.FromMinutes(5);
    if (!string.IsNullOrWhiteSpace(profile.ApiKey)) {
      _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
    }
  }

  public Uri ChatUri { get; }
  public string ProviderId => "openai-compatible";
  public AiModelProfile Profile { get; }
  public bool SupportsVision => Profile.SupportsImageInput;

  private async Task<List<object>> BuildMessagesAsync(AiProviderRequest request, CancellationToken cancellationToken) {
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

    return messages;
  }

  public async Task<AiProviderResponse> SendAsync(AiProviderRequest request, CancellationToken cancellationToken = default) {
    var messages = await BuildMessagesAsync(request, cancellationToken);
    var stopwatch = Stopwatch.StartNew();
    int budget = Profile.MaximumOutputTokens > 0 ? Profile.MaximumOutputTokens : 0;
    bool sendsReasoning = Profile.ReasoningEffort != AiReasoningEffort.Auto;
    Attempt attempt;
    try {
      attempt = await SendOnceAsync(messages, request, budget, sendsReasoning, cancellationToken);
    } catch (HttpRequestException exception) when (sendsReasoning && IsParameterRejection(exception)) {
      // Gateways differ in which reasoning switches they accept. Retry once without them
      // instead of failing the user's whole turn.
      attempt = await SendOnceAsync(messages, request, budget, includeReasoning: false, cancellationToken);
    }

    // Reasoning models spend the output budget on reasoning_content before writing content.
    // When the answer is cut off at the token limit, retry once with a larger budget so a
    // small default does not surface as "empty response".
    if (attempt.Answer is null && string.Equals(attempt.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
        && budget > 0 && budget < MaximumRetryBudget) {
      int retryBudget = Math.Clamp(budget * 4, 4096, MaximumRetryBudget);
      attempt = await SendOnceAsync(messages, request, retryBudget, sendsReasoning, cancellationToken);
    }

    if (attempt.Answer is null) {
      throw new InvalidDataException(DescribeEmptyAnswer(attempt.Raw, attempt.Reasoning, attempt.FinishReason));
    }

    return new(attempt.Answer, Profile.DisplayName, SupportsVision && !string.IsNullOrWhiteSpace(request.PreviewImagePath), stopwatch.Elapsed);
  }

  private const int MaximumRetryBudget = 16_000;

  private static bool IsTransient(HttpStatusCode status) {
    return status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
  }

  private static bool IsParameterRejection(HttpRequestException exception) {
    string message = exception.Message;
    return message.Contains("returned 400", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("returned 422", StringComparison.OrdinalIgnoreCase);
  }

  private sealed record Attempt(string? Answer, string Raw, string? Reasoning, string? FinishReason);

  private async Task<Attempt> SendOnceAsync(
      List<object> messages, AiProviderRequest request, int maxTokens, bool includeReasoning, CancellationToken cancellationToken) {
    var body = BuildBody(messages, request, maxTokens, includeReasoning, stream: false);
    using var response = await _http.PostAsJsonAsync(ChatUri, body, cancellationToken);
    string raw = await response.Content.ReadAsStringAsync(cancellationToken);
    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) {
      var fallback = FallbackChatUri();
      if (fallback is not null) {
        return await SendToAsync(fallback, body, cancellationToken, ChatUri);
      }
    }

    if (IsTransient(response.StatusCode)) {
      await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
      using var retry = await _http.PostAsJsonAsync(ChatUri, body, cancellationToken);
      string retryRaw = await retry.Content.ReadAsStringAsync(cancellationToken);
      if (retry.IsSuccessStatusCode) {
        return ParseAttempt(retryRaw);
      }

      throw new HttpRequestException(DescribeHttpFailure(retry, retryRaw, ChatUri));
    }

    if (!response.IsSuccessStatusCode) {
      throw new HttpRequestException(DescribeHttpFailure(response, raw, ChatUri));
    }

    return ParseAttempt(raw);
  }

  private static Attempt ParseAttempt(string raw) {
    using var json = JsonDocument.Parse(raw);
    if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) {
      throw new InvalidDataException(
          $"OpenAI-compatible endpoint returned no choices. Check that the Base URL points at an OpenAI-compatible /v1 endpoint and the model id is valid.\nRaw response: {Limit(raw, 800)}");
    }

    var choice = choices[0];
    var messageElement = choice.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
    string? answer = messageElement.ValueKind == JsonValueKind.Object ? ReadContent(messageElement) : null;
    string? finishReason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
        ? finish.GetString()
        : null;
    string? reasoning = messageElement.ValueKind == JsonValueKind.Object ? ReadReasoning(messageElement) : null;
    return new(string.IsNullOrWhiteSpace(answer) ? null : answer.Trim(), raw, reasoning, finishReason);
  }

  /// <summary>
  /// Reads message.content, which may be a plain string or an array of content parts
  /// ({"type":"text","text":"..."}) as returned by multimodal OpenAI-compatible servers.
  /// </summary>
  private static string? ReadContent(JsonElement message) {
    if (!message.TryGetProperty("content", out var content)) {
      return null;
    }

    return content.ValueKind switch {
      JsonValueKind.String => content.GetString(),
      JsonValueKind.Array => string.Concat(content.EnumerateArray().Select(part =>
          part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
              ? text.GetString()
              : "")),
      _ => null
    };
  }

  private static string? ReadReasoning(JsonElement message) {
    foreach (string name in new[] { "reasoning_content", "reasoning" }) {
      if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty(name, out var reasoning)) {
        if (reasoning.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reasoning.GetString())) {
          return reasoning.GetString();
        }
      }
    }

    return null;
  }

  private static string DescribeEmptyAnswer(string raw, string? reasoning, string? finishReason) {
    var hints = new List<string>();
    if (reasoning is not null) {
      hints.Add("模型把正文放进了 reasoning_content（推理模型），content 为空。请提高“最大输出 token”，或换用非推理模型。");
      hints.Add("The model returned only reasoning_content and left content empty; raise the maximum output tokens or switch to a non-reasoning model.");
    }

    if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)) {
      hints.Add("finish_reason=length：输出被 max_tokens 截断，请提高“最大输出 token”。");
      hints.Add("finish_reason=length: the answer was cut off by max_tokens.");
    } else if (string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase)) {
      hints.Add("finish_reason=content_filter：内容被服务端安全策略拦截。");
      hints.Add("finish_reason=content_filter: the endpoint blocked the content.");
    } else if (string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase)) {
      hints.Add("模型返回了 tool_calls 而不是文本；当前端点不支持工具调用，请换用普通对话模型。");
      hints.Add("The model answered with tool_calls instead of text.");
    }

    if (hints.Count == 0) {
      hints.Add("端点返回了 200 但 message.content 为空；常见原因是推理模型、token 预算不足或代理改写了响应。");
      hints.Add("The endpoint returned 200 with an empty message.content.");
    }

    return $"OpenAI-compatible endpoint returned an empty message content (finish_reason={finishReason ?? "unknown"})." +
        Environment.NewLine + string.Join(Environment.NewLine, hints) +
        Environment.NewLine + $"Raw response: {Limit(raw, 800)}";
  }

  /// <summary>
  /// Accepts every base-URL shape people paste into the settings field: a bare host,
  /// a host with <c>/v1</c>, or a complete <c>.../chat/completions</c> URL.
  /// </summary>
  public static Uri ResolveChatCompletionsUri(string endpoint) {
    string value = (endpoint ?? "").Trim().TrimEnd('/');
    if (value.Length == 0) {
      throw new InvalidOperationException("OpenAI Base URL 为空 / OpenAI Base URL is empty.");
    }

    if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) {
      return new Uri(value, UriKind.Absolute);
    }

    return value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
        ? new Uri(value + "/chat/completions", UriKind.Absolute)
        : new Uri(value + "/v1/chat/completions", UriKind.Absolute);
  }

  public async Task<AiProviderResponse> SendStreamingAsync(
      AiProviderRequest request, Action<AiStreamDelta> onDelta, CancellationToken cancellationToken = default) {
    var messages = await BuildMessagesAsync(request, cancellationToken);
    var body = BuildBody(messages, request, Profile.MaximumOutputTokens, includeReasoning: true, stream: true);
    var stopwatch = Stopwatch.StartNew();
    using var message = new HttpRequestMessage(HttpMethod.Post, ChatUri) { Content = JsonContent.Create(body) };
    using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (!response.IsSuccessStatusCode) {
      string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
      throw new HttpRequestException(DescribeHttpFailure(response, errorBody, ChatUri));
    }

    string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
    if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase)) {
      // The endpoint ignored stream=true; fall back to the buffered reply.
      string buffered = await response.Content.ReadAsStringAsync(cancellationToken);
      var parsed = ParseAttempt(buffered);
      if (parsed.Answer is null) {
        throw new InvalidDataException(DescribeEmptyAnswer(parsed.Raw, parsed.Reasoning, parsed.FinishReason));
      }

      return new(parsed.Answer, Profile.DisplayName, SupportsVision && !string.IsNullOrWhiteSpace(request.PreviewImagePath), stopwatch.Elapsed);
    }

    var answer = new StringBuilder();
    var reasoning = new StringBuilder();
    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var reader = new StreamReader(stream, Encoding.UTF8);
    while (await reader.ReadLineAsync(cancellationToken) is { } line) {
      cancellationToken.ThrowIfCancellationRequested();
      if (!line.StartsWith("data:", StringComparison.Ordinal)) {
        continue;
      }

      string payload = line[5..].Trim();
      if (payload.Length == 0 || payload == "[DONE]") {
        if (payload == "[DONE]") {
          break;
        }

        continue;
      }

      using var chunk = JsonDocument.Parse(payload);
      if (chunk.RootElement.TryGetProperty("error", out var error)) {
        throw new InvalidDataException($"Streaming endpoint reported an error: {Limit(error.GetRawText(), 400)}");
      }

      if (!chunk.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) {
        continue;
      }

      var choice = choices[0];
      if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) {
        continue;
      }

      string? reasoningPiece = ReadDeltaText(delta, "reasoning_content");
      if (reasoningPiece is { Length: > 0 }) {
        reasoning.Append(reasoningPiece);
        onDelta(new(true, reasoningPiece));
      }

      string? contentPiece = ReadDeltaText(delta, "content");
      if (contentPiece is { Length: > 0 }) {
        answer.Append(contentPiece);
        onDelta(new(false, contentPiece));
      }
    }

    if (answer.Length == 0) {
      throw new InvalidDataException(DescribeEmptyAnswer("[stream]", reasoning.ToString(), finishReason: null));
    }

    return new(answer.ToString().Trim(), Profile.DisplayName, SupportsVision && !string.IsNullOrWhiteSpace(request.PreviewImagePath), stopwatch.Elapsed);
  }

  private static string? ReadDeltaText(JsonElement delta, string name) {
    if (!delta.TryGetProperty(name, out var value)) {
      return null;
    }

    return value.ValueKind switch {
      JsonValueKind.String => value.GetString(),
      JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(part =>
          part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
              ? text.GetString()
              : "")),
      _ => null
    };
  }

  private Dictionary<string, object?> BuildBody(
      List<object> messages, AiProviderRequest request, int maxTokens, bool includeReasoning, bool stream) {
    var body = new Dictionary<string, object?> {
      ["model"] = Profile.EffectiveModelId,
      ["messages"] = messages,
      ["temperature"] = request.Temperature
    };
    if (maxTokens > 0) {
      body["max_tokens"] = maxTokens;
    }

    if (stream) {
      body["stream"] = true;
    }

    if (request.RequireJson) {
      body["response_format"] = new { type = "json_object" };
    }

    if (includeReasoning) {
      if (Profile.ReasoningEffort == AiReasoningEffort.Off) {
        body["thinking"] = new { type = "disabled" };
      } else if (Profile.ReasoningEffortParameter is { } effort) {
        body["reasoning_effort"] = effort;
      }
    }

    return body;
  }

  /// <summary>The URL without the <c>/v1</c> segment, used once as a fallback after a 404.</summary>
  private Uri? FallbackChatUri() {
    string path = ChatUri.AbsolutePath;
    if (!path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)) {
      return null;
    }

    return new Uri(ChatUri.GetLeftPart(UriPartial.Authority) + path[..^"/v1/chat/completions".Length] + "/chat/completions");
  }

  private async Task<Attempt> SendToAsync(
      Uri uri, Dictionary<string, object?> body, CancellationToken cancellationToken, Uri? alsoTried = null) {
    using var response = await _http.PostAsJsonAsync(uri, body, cancellationToken);
    string raw = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode) {
      throw new HttpRequestException(DescribeHttpFailure(response, raw, uri, alsoTried));
    }

    return ParseAttempt(raw);
  }

  private static string DescribeHttpFailure(HttpResponseMessage response, string raw, Uri uri, Uri? alsoTried = null) {
    var hint = new StringBuilder();
    hint.Append($"OpenAI-compatible endpoint returned {(int)response.StatusCode} for POST {uri}: {Limit(raw, 400)}");
    if (alsoTried is not null) {
      hint.AppendLine();
      hint.Append($"已同时尝试过：{alsoTried}");
    }
    if (response.StatusCode == HttpStatusCode.NotFound) {
      hint.AppendLine();
      hint.Append("请求路径不存在。Base URL 只需填到 /v1（例如 https://host/v1），不要填完整的 /chat/completions；" +
                  "若网关不带 /v1，填 https://host 即可。已自动尝试过带与不带 /v1 两种路径。");
      hint.AppendLine();
      hint.Append("The path was not found: use the base URL only (https://host or https://host/v1). Both variants were tried.");
    } else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
      hint.AppendLine();
      hint.Append("鉴权失败：检查 API Key / Authentication failed: check the API key.");
    }

    return hint.ToString();
  }

  private static string Limit(string value, int maximum) {
    return value.Length <= maximum ? value : value[..maximum];
  }

  public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}

public sealed record AiProviderHandle(IAiProvider Provider) : IAsyncDisposable {
  public async ValueTask DisposeAsync() {
    await Provider.DisposeAsync();
  }
}

public sealed class AiProviderFactory {
  public Task<AiProviderHandle> CreateAsync(
      AiModelProfile profile,
      VisionStudio.Core.IWorkflowAutomationApi workflow,
      string dataDirectory,
      string hostVersion,
      CancellationToken cancellationToken = default) {
    if (string.IsNullOrWhiteSpace(profile.Endpoint)) {
      throw new InvalidOperationException(
          "远程模型缺少 OpenAI Base URL，请在“文件 → 设置”中填写。\nThe selected model has no OpenAI Base URL; configure it in File → Settings.");
    }

    return Task.FromResult(new AiProviderHandle(new OpenAiCompatibleProvider(profile)));
  }
}
