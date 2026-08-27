[CmdletBinding()]
param([string]$Version = '2.0.0')

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = Join-Path $workspaceRoot 'artifacts\web'
$packageName = "VisionStudio.Web-$Version-ubuntu-x64"
$staging = Join-Path $releaseRoot $packageName
$archive = Join-Path $releaseRoot "$packageName.tar.gz"
$project = Join-Path $workspaceRoot 'src\VisionStudio.Web\VisionStudio.Web.csproj'
[System.IO.Directory]::CreateDirectory($releaseRoot) | Out-Null

$fullStaging = [System.IO.Path]::GetFullPath($staging)
$safePrefix = [System.IO.Path]::GetFullPath($releaseRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (!$fullStaging.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase) -or ![System.IO.Path]::GetFileName($fullStaging).StartsWith('VisionStudio.Web-', [StringComparison]::Ordinal)) {
    throw "Unsafe web staging path: $fullStaging"
}
if (Test-Path -LiteralPath $fullStaging) { Remove-Item -LiteralPath $fullStaging -Recurse -Force }
[System.IO.Directory]::CreateDirectory($fullStaging) | Out-Null

& dotnet publish $project -c Release -r ubuntu-x64 --self-contained true --nologo `
    -p:Version=$Version -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $fullStaging
if ($LASTEXITCODE -ne 0) { throw 'Web publish failed.' }
Copy-Item -LiteralPath (Join-Path $workspaceRoot 'deploy\web\.env.example') -Destination $fullStaging
Copy-Item -LiteralPath (Join-Path $workspaceRoot 'deploy\web\start.sh') -Destination $fullStaging
Copy-Item -LiteralPath (Join-Path $workspaceRoot 'deploy\web\install-dependencies.sh') -Destination $fullStaging
Copy-Item -LiteralPath (Join-Path $workspaceRoot 'deploy\web\visionstudio-web.service') -Destination $fullStaging
Copy-Item -LiteralPath (Join-Path $workspaceRoot 'deploy\web\nginx.conf.example') -Destination $fullStaging
Copy-Item -LiteralPath (Join-Path $workspaceRoot 'deploy\web\README.txt') -Destination $fullStaging

Add-Type -TypeDefinition @'
using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
public static class VisionStudioWebTar
{
    public static void Create(string source, string archive)
    {
        using var output = File.Create(archive);
        using var gzip = new GZipStream(output, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzip, leaveOpen: false);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            var executable = relative is "VisionStudio.Web" or "start.sh" or "install-dependencies.sh";
            var entry = new PaxTarEntry(TarEntryType.RegularFile, relative)
            {
                Mode = executable
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                DataStream = File.OpenRead(path)
            };
            using (entry.DataStream) writer.WriteEntry(entry);
        }
    }
}
'@
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
[VisionStudioWebTar]::Create($fullStaging, $archive)
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$archive.sha256", "$hash  $([System.IO.Path]::GetFileName($archive))`n", [System.Text.UTF8Encoding]::new($false))
Write-Output "Web package: $archive"
Write-Output "SHA256: $hash"
