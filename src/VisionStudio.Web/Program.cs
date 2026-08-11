using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using VisionStudio.Core;
using VisionStudio.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<WebProjectStore>();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = WebProjectStore.MaximumUploadBytes + 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = WorkflowJson.Options.PropertyNamingPolicy;
    options.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(System.Net.IPAddress.Loopback);
    options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});

var app = builder.Build();
var configuredToken = builder.Configuration["VisionStudio:Token"] ?? Environment.GetEnvironmentVariable("VISIONSTUDIO_WEB_TOKEN");
if (string.IsNullOrWhiteSpace(configuredToken) && !app.Environment.IsDevelopment())
    throw new InvalidOperationException("VISIONSTUDIO_WEB_TOKEN is required outside Development.");

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/api/health")
    {
        await next();
        return;
    }
    if (string.IsNullOrWhiteSpace(configuredToken)) { await next(); return; }
    var supplied = context.Request.Headers["X-VisionStudio-Token"].ToString();
    var expectedBytes = Encoding.UTF8.GetBytes(configuredToken);
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    if (expectedBytes.Length != suppliedBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Authentication required.", code = "unauthorized" });
        return;
    }
    await next();
});
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception exception) when (!context.Response.HasStarted)
    {
        var (status, code, message, details) = exception switch
        {
            WorkflowRevisionConflictException conflict => (409, "revision_conflict", conflict.Message, (object?)new { conflict.ExpectedRevision, conflict.ActualRevision }),
            WorkflowValidationException validation => (400, "workflow_invalid", "Workflow validation failed.", validation.Errors),
            ArgumentException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or NotSupportedException => (400, "invalid_request", exception.Message, null),
            KeyNotFoundException or FileNotFoundException => (404, "not_found", exception.Message, null),
            _ => (500, "server_error", "The server could not complete the request.", null)
        };
        if (status == 500) app.Logger.LogError(exception, "Unhandled web API error");
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message, code, details });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
        context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "public,max-age=86400"
});

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", version = "1.0.0" }));
app.MapGet("/api/projects", async (WebProjectStore store, CancellationToken cancellationToken) => Results.Ok(await store.ListAsync(cancellationToken)));
app.MapPost("/api/projects", async (CreateProjectRequest request, WebProjectStore store, CancellationToken cancellationToken) =>
{
    var context = await store.CreateAsync(request.Name, cancellationToken);
    return Results.Created($"/api/projects/{context.Handle.Project.Id}", new WebProjectSummary(context.Handle.Project.Id, context.Handle.Project.Name, context.Handle.Project.Description, context.Handle.Project.ModifiedAt));
});
app.MapGet("/api/projects/{projectId:guid}/catalog", async (Guid projectId, WebProjectStore store, CancellationToken cancellationToken) =>
    Results.Ok(await (await store.GetAsync(projectId, cancellationToken)).Automation.GetOperatorCatalogAsync(cancellationToken)));
app.MapGet("/api/projects/{projectId:guid}/snapshot", async (Guid projectId, WebProjectStore store, CancellationToken cancellationToken) =>
    Results.Ok(await (await store.GetAsync(projectId, cancellationToken)).Automation.GetSnapshotAsync(cancellationToken)));
app.MapPost("/api/projects/{projectId:guid}/mutations", async (Guid projectId, WorkflowMutationRequest request, WebProjectStore store, CancellationToken cancellationToken) =>
    Results.Ok(await (await store.GetAsync(projectId, cancellationToken)).Automation.ApplyAsync(request, cancellationToken)));
app.MapPost("/api/projects/{projectId:guid}/execute", async (Guid projectId, ExecuteNodeRequest request, WebProjectStore store, CancellationToken cancellationToken) =>
    Results.Ok(await (await store.GetAsync(projectId, cancellationToken)).Automation.ExecuteAsync(request.RequestId, request.TargetNodeId, cancellationToken)));
app.MapPost("/api/projects/{projectId:guid}/undo", async (Guid projectId, WebProjectStore store, CancellationToken cancellationToken) =>
    Results.Ok(await (await store.GetAsync(projectId, cancellationToken)).UndoAsync(cancellationToken)));
app.MapPost("/api/projects/{projectId:guid}/images", async (Guid projectId, IFormFile file, WebProjectStore store, CancellationToken cancellationToken) =>
{
    var context = await store.GetAsync(projectId, cancellationToken);
    return Results.Ok(new { path = await store.SaveImageAsync(context, file, cancellationToken) });
}).DisableAntiforgery();
app.MapPost("/api/projects/{projectId:guid}/export", async (Guid projectId, WebProjectStore store, CancellationToken cancellationToken) =>
{
    var context = await store.GetAsync(projectId, cancellationToken);
    _ = await store.ExportAsync(context, cancellationToken);
    return Results.Ok(new { path = "output/GeneratedVisionAlgorithm.cs", downloadUrl = $"/api/projects/{projectId}/files?path={Uri.EscapeDataString("output/GeneratedVisionAlgorithm.cs")}" });
});
app.MapGet("/api/projects/{projectId:guid}/operations", async (Guid projectId, WebProjectStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.OperationsAsync(await store.GetAsync(projectId, cancellationToken), cancellationToken)));
app.MapGet("/api/projects/{projectId:guid}/files", async (Guid projectId, string path, WebProjectStore store, CancellationToken cancellationToken) =>
{
    var context = await store.GetAsync(projectId, cancellationToken);
    var file = store.ResolveReadableFile(context, path);
    return Results.File(file, ContentType(file), enableRangeProcessing: true);
});
app.MapFallbackToFile("index.html");

app.Run();

static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".bmp" => "image/bmp",
    ".tif" or ".tiff" => "image/tiff", ".webp" => "image/webp", ".json" => "application/json",
    ".cs" => "text/plain; charset=utf-8", _ => "application/octet-stream"
};

public sealed record CreateProjectRequest(string Name);
public sealed record ExecuteNodeRequest(string RequestId, string TargetNodeId);
public partial class Program;
