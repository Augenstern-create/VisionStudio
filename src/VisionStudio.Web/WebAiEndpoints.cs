using System.Text;
using System.Text.Json;
using VisionStudio.Ai;
using VisionStudio.Ai.Abstractions;
using VisionStudio.Core;
using VisionStudio.Operators.OpenCv;

namespace VisionStudio.Web;

/// <summary>
/// AI assistant endpoints. The web app uses the same agent, provider and read-only tools as the
/// desktop shell; model configuration lives on the server (encrypted) and never reaches the browser.
/// </summary>
internal static class WebAiEndpoints {
  private static readonly JsonSerializerOptions EventJson = new() {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  };

  public static WebApplication MapWebAiApi(this WebApplication app) {
    app.MapGet("/api/ai/models", async (CancellationToken cancellationToken) => {
      var profiles = await new AiModelProfileStore().LoadAsync(cancellationToken);
      return Results.Ok(profiles.Select(profile => new {
        profile.Id,
        profile.DisplayName,
        profile.EffectiveModelId,
        Endpoint = HostOf(profile.Endpoint),
        profile.SupportsImageInput
      }));
    });

    app.MapPost("/api/projects/{projectId:guid}/ai/chat", async (
        Guid projectId,
        WebAiChatRequest request,
        WebProjectStore store,
        HttpContext http,
        CancellationToken cancellationToken) => {
          if (string.IsNullOrWhiteSpace(request.Text)) {
            return Results.BadRequest(new { error = "text is required" });
          }

          var context = await store.GetAsync(projectId, cancellationToken);
          var profiles = await new AiModelProfileStore().LoadAsync(cancellationToken);
          var profile = profiles.FirstOrDefault(item => item.Id == request.ModelId) ?? profiles.FirstOrDefault();
          if (profile is null) {
            return Results.BadRequest(new { error = "尚未在服务器上配置远程模型 / no remote model is configured on the server" });
          }

          var snapshot = await context.Automation.GetSnapshotAsync(cancellationToken);
          var registry = OpenCvOperatorCatalog.CreateRegistry();
          var tools = new OpenCvAiWorkflowTools(registry, context.Handle.RootPath, () => snapshot.Workflow);

          http.Response.ContentType = "text/event-stream";
          http.Response.Headers.CacheControl = "no-cache";
          http.Response.Headers["X-Accel-Buffering"] = "no";
          // Progress<T> invokes callbacks concurrently when there is no synchronization context,
          // so deltas are queued and written by a single loop to keep the token order intact.
          var channel = System.Threading.Channels.Channel.CreateUnbounded<(string Kind, string Text)>();
          var writer = Task.Run(async () => {
            await foreach (var (kind, text) in channel.Reader.ReadAllAsync(cancellationToken)) {
              if (text.Length == 0 && kind != "done") {
                continue;
              }

              try {
                await http.Response.WriteAsync(
                    $"data: {JsonSerializer.Serialize(new { kind, text }, EventJson)}\n\n", cancellationToken);
                await http.Response.Body.FlushAsync(cancellationToken);
              } catch (Exception) {
                // The browser may disconnect mid-stream; nothing else to do.
              }
            }
          }, cancellationToken);

          void Send(string kind, string text) => channel.Writer.TryWrite((kind, text));

          try {
            await using var provider = new OpenAiCompatibleProvider(profile);
            var agent = new AiWorkflowAgent(context.Automation, provider, tools);
            // Progress<T> would hop to the thread pool and lose token order; report inline instead.
            var progress = new InlineProgress<AiStreamDelta>(delta => Send(delta.IsReasoning ? "reasoning" : "content", delta.Text));
            var toolProgress = new InlineProgress<string>(nodeId => Send("tool", nodeId));
            var response = await agent.ProcessAsync(
                new(request.Text, request.SelectedNodeId, Conversation: null), progress, toolProgress, cancellationToken);

            if (response.Plan is not null) {
              Send("plan", response.Plan.Summary);
            }

            Send("done", "");
          } catch (Exception exception) {
            Send("error", exception.Message);
          } finally {
            channel.Writer.TryComplete();
            try {
              await writer;
            } catch (Exception) {
              // Client disconnected; the response is already unusable.
            }
          }

          return Results.Empty;
        });

    return app;
  }

  private static string HostOf(string endpoint) {
    return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : endpoint;
  }
}

public sealed record WebAiChatRequest(string? ModelId, string Text, string? SelectedNodeId = null);

/// <summary>Synchronous <see cref="IProgress{T}"/> so streamed deltas keep their order.</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T> {
  public void Report(T value) => report(value);
}
