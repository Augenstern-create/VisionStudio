using VisionStudio.Core;

namespace VisionStudio.Web;

internal static class WebApiEndpoints {
  public static WebApplication MapVisionStudioApi(this WebApplication app) {
    app.MapGet("/api/health", () => Results.Ok(new {
      status = "healthy",
      version = typeof(WebProjectStore).Assembly.GetName().Version?.ToString(3) ?? "2.0.0"
    }));
    app.MapGet("/api/projects", async (
        WebProjectStore store,
        CancellationToken cancellationToken) =>
      Results.Ok(await store.ListAsync(cancellationToken)));
    app.MapPost("/api/projects", async (
        CreateProjectRequest request,
        WebProjectStore store,
        CancellationToken cancellationToken) => {
          var context = await store.CreateAsync(request.Name, cancellationToken);
          return Results.Created(
              $"/api/projects/{context.Handle.Project.Id}",
              new WebProjectSummary(
                  context.Handle.Project.Id,
                  context.Handle.Project.Name,
                  context.Handle.Project.Description,
                  context.Handle.Project.ModifiedAt));
        });
    app.MapGet("/api/projects/{projectId:guid}/catalog", async (
        Guid projectId,
        WebProjectStore store,
        CancellationToken cancellationToken) =>
      Results.Ok(await (await store.GetAsync(projectId, cancellationToken))
          .Automation.GetOperatorCatalogAsync(cancellationToken)));
    app.MapGet("/api/projects/{projectId:guid}/snapshot", async (
        Guid projectId,
        WebProjectStore store,
        CancellationToken cancellationToken) =>
      Results.Ok(await (await store.GetAsync(projectId, cancellationToken))
          .Automation.GetSnapshotAsync(cancellationToken)));
    app.MapPost("/api/projects/{projectId:guid}/mutations", async (
        Guid projectId,
        WorkflowMutationRequest request,
        WebProjectStore store,
        CancellationToken cancellationToken) =>
      Results.Ok(await (await store.GetAsync(projectId, cancellationToken))
          .Automation.ApplyAsync(request, cancellationToken)));
    app.MapPost("/api/projects/{projectId:guid}/execute", async (
        Guid projectId,
        ExecuteNodeRequest request,
        WebProjectStore store,
        CancellationToken cancellationToken) =>
      Results.Ok(await (await store.GetAsync(projectId, cancellationToken))
          .Automation.ExecuteAsync(request.RequestId, request.TargetNodeId, cancellationToken)));
    app.MapPost("/api/projects/{projectId:guid}/undo", async (
        Guid projectId,
        WebProjectStore store,
        CancellationToken cancellationToken) =>
      Results.Ok(await (await store.GetAsync(projectId, cancellationToken))
          .UndoAsync(cancellationToken)));
    app.MapPost("/api/projects/{projectId:guid}/images", async (
        Guid projectId,
        IFormFile file,
        WebProjectStore store,
        CancellationToken cancellationToken) => {
          var context = await store.GetAsync(projectId, cancellationToken);
          return Results.Ok(new {
            path = await store.SaveImageAsync(context, file, cancellationToken)
          });
        }).DisableAntiforgery();
    app.MapPost("/api/projects/{projectId:guid}/export", async (
        Guid projectId,
        string? targetNodeId,
        WebProjectStore store,
        CancellationToken cancellationToken) => {
          var context = await store.GetAsync(projectId, cancellationToken);
          _ = await store.ExportAsync(context, targetNodeId, cancellationToken);
          const string path = "output/GeneratedVisionAlgorithm.cs";
          return Results.Ok(new {
            path,
            downloadUrl = $"/api/projects/{projectId}/files?path={Uri.EscapeDataString(path)}"
          });
        });
    app.MapGet("/api/projects/{projectId:guid}/operations", async (
        Guid projectId,
        WebProjectStore store,
        CancellationToken cancellationToken) =>
      Results.Ok(await store.OperationsAsync(
          await store.GetAsync(projectId, cancellationToken),
          cancellationToken)));
    app.MapGet("/api/projects/{projectId:guid}/files", async (
        Guid projectId,
        string path,
        WebProjectStore store,
        CancellationToken cancellationToken) => {
          var context = await store.GetAsync(projectId, cancellationToken);
          string file = store.ResolveReadableFile(context, path);
          return Results.File(file, ContentType(file), enableRangeProcessing: true);
        });
    app.MapFallbackToFile("index.html");
    return app;
  }

  private static string ContentType(string path) {
    return Path.GetExtension(path).ToLowerInvariant() switch {
      ".png" => "image/png",
      ".jpg" or ".jpeg" => "image/jpeg",
      ".bmp" => "image/bmp",
      ".tif" or ".tiff" => "image/tiff",
      ".webp" => "image/webp",
      ".json" => "application/json",
      ".cs" => "text/plain; charset=utf-8",
      _ => "application/octet-stream"
    };
  }
}

public sealed record CreateProjectRequest(string Name);
public sealed record ExecuteNodeRequest(string RequestId, string TargetNodeId);
