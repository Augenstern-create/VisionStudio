using System.Security.Cryptography;
using System.Text;
using VisionStudio.Core;

namespace VisionStudio.Web;

internal static class WebApplicationPipeline {
  public static WebApplication UseVisionStudioPipeline(
      this WebApplication app,
      string? configuredToken) {
    app.UseForwardedHeaders();
    app.UseSecurityHeaders();
    app.UseRateLimiter();
    app.UseTokenAuthentication(configuredToken);
    app.UseApiErrorHandling();
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions {
      OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
          context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
              ? "no-cache"
              : "public,max-age=86400"
    });
    return app;
  }

  private static void UseSecurityHeaders(this WebApplication app) {
    app.Use(async (context, next) => {
      context.Response.Headers["X-Content-Type-Options"] = "nosniff";
      context.Response.Headers["Referrer-Policy"] = "no-referrer";
      context.Response.Headers["X-Frame-Options"] = "DENY";
      context.Response.Headers["Content-Security-Policy"] =
          "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; " +
          "script-src 'self'; connect-src 'self'; frame-ancestors 'none'; " +
          "base-uri 'self'; form-action 'self'";
      await next();
    });
  }

  private static void UseTokenAuthentication(
      this WebApplication app,
      string? configuredToken) {
    app.Use(async (context, next) => {
      if (!context.Request.Path.StartsWithSegments("/api") ||
          context.Request.Path == "/api/health") {
        await next();
        return;
      }
      if (string.IsNullOrWhiteSpace(configuredToken)) {
        await next();
        return;
      }

      string supplied = context.Request.Headers["X-VisionStudio-Token"].ToString();
      byte[] expectedBytes = Encoding.UTF8.GetBytes(configuredToken);
      byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
      if (expectedBytes.Length != suppliedBytes.Length ||
          !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes)) {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new {
          error = "Authentication required.",
          code = "unauthorized"
        });
        return;
      }
      await next();
    });
  }

  private static void UseApiErrorHandling(this WebApplication app) {
    app.Use(async (context, next) => {
      try {
        await next();
      } catch (Exception exception) when (!context.Response.HasStarted) {
        var (status, code, message, details) = exception switch {
          WorkflowRevisionConflictException conflict => (
            409,
            "revision_conflict",
            conflict.Message,
            (object?)new { conflict.ExpectedRevision, conflict.ActualRevision }),
          WorkflowValidationException validation => (
            400,
            "workflow_invalid",
            "Workflow validation failed.",
            validation.Errors),
          ArgumentException or InvalidDataException or InvalidOperationException or
              System.Text.Json.JsonException or NotSupportedException =>
            (400, "invalid_request", exception.Message, null),
          KeyNotFoundException or FileNotFoundException =>
            (404, "not_found", exception.Message, null),
          _ => (500, "server_error", "The server could not complete the request.", null)
        };
        if (status == 500) {
          app.Logger.LogError(exception, "Unhandled web API error");
        }

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message, code, details });
      }
    });
  }
}
