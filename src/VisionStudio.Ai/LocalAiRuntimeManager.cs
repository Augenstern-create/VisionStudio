using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using VisionStudio.Ai.Abstractions;

namespace VisionStudio.Ai;

public sealed class LocalAiRuntimeManager(string? installDirectory = null, HttpClient? httpClient = null) {
  private readonly string _installDirectory = Path.GetFullPath(installDirectory ?? AiSoftwarePaths.RuntimeDirectory);
  private readonly HttpClient _http = httpClient ?? CreateHttpClient();

  public string? FindLlamaServer(string? configuredPath = null) {
    string executable = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
    var candidates = new List<string?>
    {
            configuredPath,
            Environment.GetEnvironmentVariable("LLAMA_SERVER_PATH"),
            Path.Combine(_installDirectory, executable),
            Path.Combine(AppContext.BaseDirectory, "llama", executable),
            OperatingSystem.IsWindows() ? Path.Combine("D:\\Programs\\llama", executable) : null,
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "llama.cpp", executable) : null
        };
    candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(path => Path.Combine(path, executable)));
    foreach (string? candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate))) {
      if (File.Exists(candidate)) {
        return Path.GetFullPath(candidate!);
      }
    }

    foreach (string? root in candidates.Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase)) {
      try {
        string? match = Directory.EnumerateFiles(root!, executable, SearchOption.AllDirectories).FirstOrDefault();
        if (match is not null) {
          return Path.GetFullPath(match);
        }
      } catch (UnauthorizedAccessException) { }
    }
    return null;
  }

  public async Task<AiModelProfile> PrepareProfileAsync(AiModelProfile profile,
      StandardAiModelDefinition? standard = null, IProgress<string>? progress = null,
      CancellationToken cancellationToken = default) {
    if (profile.Source == AiModelSource.Remote) {
      return profile;
    }

    Directory.CreateDirectory(AiSoftwarePaths.ModelsDirectory);
    if (File.Exists(profile.ModelPath) && standard is not null) {
      progress?.Report("正在校验模型…");
      await ValidateFileAsync(
          profile.ModelPath, standard.ModelSizeBytes, standard.ModelSha256, cancellationToken);
    } else if (!File.Exists(profile.ModelPath)) {
      string? url = standard?.DownloadUrl ?? profile.DownloadUrl;
      if (string.IsNullOrWhiteSpace(url)) {
        throw new FileNotFoundException("本地模型不存在，且没有配置模型下载地址。", profile.ModelPath);
      }

      progress?.Report("正在下载模型…");
      await DownloadFileAsync(
          url, profile.ModelPath, standard?.ModelSizeBytes, standard?.ModelSha256, cancellationToken);
    }
    if (!string.IsNullOrWhiteSpace(profile.MmprojPath)) {
      if (File.Exists(profile.MmprojPath) && standard is not null) {
        progress?.Report("正在校验视觉投影模型…");
        await ValidateFileAsync(
            profile.MmprojPath, standard.MmprojSizeBytes, standard.MmprojSha256, cancellationToken);
      } else if (!File.Exists(profile.MmprojPath)) {
        if (string.IsNullOrWhiteSpace(standard?.MmprojDownloadUrl)) {
          throw new FileNotFoundException("视觉投影模型不存在，且没有配置下载地址。", profile.MmprojPath);
        }

        progress?.Report("正在下载视觉投影模型…");
        await DownloadFileAsync(
            standard.MmprojDownloadUrl, profile.MmprojPath,
            standard.MmprojSizeBytes, standard.MmprojSha256, cancellationToken);
      }
    }
    string? server = FindLlamaServer(profile.LlamaServerPath);
    if (server is null) {
      progress?.Report("正在安装 llama.cpp 本地运行时…");
      server = await DownloadLatestRuntimeAsync(cancellationToken);
    }
    progress?.Report("本地模型已就绪");
    return profile with { LlamaServerPath = server, ProviderPluginId = "visionstudio.ai.llamacpp", Endpoint = "http://127.0.0.1:11435" };
  }

  public async Task<string> DownloadLatestRuntimeAsync(CancellationToken cancellationToken = default) {
    Directory.CreateDirectory(_installDirectory);
    var releases = await _http.GetFromJsonAsync<List<GitHubRelease>>(
        "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=5", cancellationToken) ?? [];
    string suffix = RuntimeAssetSuffix();
    var asset = releases.SelectMany(release => release.Assets ?? [])
        .FirstOrDefault(item => item.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"llama.cpp 最近的版本中没有适用于当前平台的运行时（{suffix}）。");
    string archive = Path.Combine(Path.GetTempPath(), $"visionstudio-llama-{Guid.NewGuid():N}{(suffix.EndsWith(".zip") ? ".zip" : ".tar.gz")}");
    try {
      await DownloadFileAsync(asset.BrowserDownloadUrl, archive, null, null, cancellationToken);
      if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
        ZipFile.ExtractToDirectory(archive, _installDirectory, overwriteFiles: true);
      } else {
        await using var stream = File.OpenRead(archive);
        await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, _installDirectory, overwriteFiles: true);
      }
      string? server = FindLlamaServer() ?? throw new InvalidDataException("下载的 llama.cpp 包中没有 llama-server。 ");
      if (!OperatingSystem.IsWindows()) {
        File.SetUnixFileMode(server,
          UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
          UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
      }

      return server;
    } finally {
      if (File.Exists(archive)) {
        File.Delete(archive);
      }
    }
  }

  private async Task DownloadFileAsync(
      string url, string destination, long? expectedSize, string? expectedSha256,
      CancellationToken cancellationToken) {
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    string temporary = destination + ".download";
    try {
      using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
      response.EnsureSuccessStatusCode();
      await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
      await using (var target = File.Create(temporary)) {
        await source.CopyToAsync(target, cancellationToken);
      }

      await ValidateFileAsync(temporary, expectedSize, expectedSha256, cancellationToken);
      File.Move(temporary, destination, overwrite: true);
    } finally {
      if (File.Exists(temporary)) {
        File.Delete(temporary);
      }
    }
  }

  private static async Task ValidateFileAsync(
      string path, long? expectedSize, string? expectedSha256,
      CancellationToken cancellationToken) {
    if (expectedSize is not null && new FileInfo(path).Length != expectedSize.Value) {
      throw new InvalidDataException(
          $"下载文件大小校验失败：应为 {expectedSize.Value} 字节，实际为 {new FileInfo(path).Length} 字节。");
    }
    if (string.IsNullOrWhiteSpace(expectedSha256)) {
      return;
    }

    await using var stream = File.OpenRead(path);
    string actual = Convert.ToHexString(
        await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidDataException($"下载文件 SHA-256 校验失败：{actual}。");
    }
  }

  private static string RuntimeAssetSuffix() {
    bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    if (OperatingSystem.IsWindows()) {
      return arm ? "-bin-win-cpu-arm64.zip" : "-bin-win-cpu-x64.zip";
    }

    if (OperatingSystem.IsLinux()) {
      return arm ? "-bin-ubuntu-arm64.tar.gz" : "-bin-ubuntu-x64.tar.gz";
    }

    if (OperatingSystem.IsMacOS()) {
      return arm ? "-bin-macos-arm64.tar.gz" : "-bin-macos-x64.tar.gz";
    }

    throw new PlatformNotSupportedException("当前平台没有可自动安装的 llama.cpp 运行时。");
  }

  private static HttpClient CreateHttpClient() {
    var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VisionStudio", "2.0"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    return client;
  }

  private sealed record GitHubRelease(GitHubAsset[]? Assets);
  private sealed record GitHubAsset(string Name, [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
