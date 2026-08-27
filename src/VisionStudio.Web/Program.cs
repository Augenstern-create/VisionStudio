using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using VisionStudio.Core;
using VisionStudio.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<WebProjectStore>();
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = WebProjectStore.MaximumUploadBytes + 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options => {
  options.SerializerOptions.PropertyNamingPolicy = WorkflowJson.Options.PropertyNamingPolicy;
  options.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
  options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddRateLimiter(options => {
  options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
  options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
      RateLimitPartition.GetFixedWindowLimiter(
          context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
          _ => new() {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
          }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options => {
  options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
  options.KnownProxies.Add(System.Net.IPAddress.Loopback);
  options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});

var app = builder.Build();
string? configuredToken = builder.Configuration["VisionStudio:Token"] ??
    Environment.GetEnvironmentVariable("VISIONSTUDIO_WEB_TOKEN");
if (string.IsNullOrWhiteSpace(configuredToken) && !app.Environment.IsDevelopment()) {
  throw new InvalidOperationException("VISIONSTUDIO_WEB_TOKEN is required outside Development.");
}

app.UseVisionStudioPipeline(configuredToken);
app.MapVisionStudioApi();
app.Run();

public partial class Program;
