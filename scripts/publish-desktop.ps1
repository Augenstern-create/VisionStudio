[CmdletBinding()]
param(
    [ValidateSet('Windows', 'Linux', 'All')]
    [string]$Platform = 'All',
    [string]$Version = '2.0.0'
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot 'artifacts\releases'))
$appProject = Join-Path $workspaceRoot 'src\VisionStudio.App\VisionStudio.App.csproj'
$cliProject = Join-Path $workspaceRoot 'src\VisionStudio.Cli\VisionStudio.Cli.csproj'
$runtimeProject = Join-Path $workspaceRoot 'src\VisionStudio.Runtimes.App\VisionStudio.Runtimes.App.csproj'
[System.IO.Directory]::CreateDirectory($releaseRoot) | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

public static class VisionStudioTarPackage
{
    public static void Create(string sourceDirectory, string archivePath)
    {
        using var output = File.Create(archivePath);
        using var gzip = new GZipStream(output, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzip, leaveOpen: false);
        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/');
            var entry = new PaxTarEntry(TarEntryType.RegularFile, relative)
            {
                Mode = relative == "VisionStudio" || relative == "VisionStudio.Cli" ||
                    relative == "VisionStudio.Runtimes.App" || relative.EndsWith(".sh", StringComparison.Ordinal)
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                      UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite |
                      UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                DataStream = File.OpenRead(path)
            };
            using (entry.DataStream) writer.WriteEntry(entry);
        }
    }
}
'@

function Reset-StagingDirectory([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $expectedPrefix = $releaseRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (!$fullPath.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        ![System.IO.Path]::GetFileName($fullPath).StartsWith('VisionStudio-', [StringComparison]::Ordinal)) {
        throw "Refusing to replace unsafe staging path: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath -Recurse -Force }
    [System.IO.Directory]::CreateDirectory($fullPath) | Out-Null
}

function Publish-Platform([string]$Name, [string]$Rid) {
    $packageName = "VisionStudio-$Version-$Rid"
    $staging = Join-Path $releaseRoot $packageName
    Reset-StagingDirectory $staging

    & dotnet publish $appProject -c Release -r $Rid --self-contained true --nologo `
        -p:Version=$Version -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $staging
    if ($LASTEXITCODE -ne 0) { throw "$Name publish failed." }
    & dotnet publish $cliProject -c Release -r $Rid --self-contained true --nologo `
        -p:Version=$Version -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $staging
    if ($LASTEXITCODE -ne 0) { throw "$Name CLI publish failed." }
    & dotnet publish $runtimeProject -c Release -r $Rid --self-contained true --nologo `
        -p:Version=$Version -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $staging
    if ($LASTEXITCODE -ne 0) { throw "$Name Runtime publish failed." }
    $readme = @"
Vision Studio $Version ($Rid)

This is a self-contained release; a separate .NET installation is not required.
Projects use the .vstudio extension. Keep all files in this directory together.
VisionStudio is the workflow editor. VisionStudio.Runtimes.App is the project runtime for single-frame and continuous inspection. VisionStudio.Cli is the command-line validation, workflow run and export tool.
AI models are configured under the installation directory's config folder through File > Settings. The left activity bar switches between Operators and AI Chat.
Local AI is optional. Install a provider plugin under the installation directory's plugins folder, or configure a remote OpenAI-compatible endpoint.
AI models, llama.cpp, CUDA libraries, and provider plugins are downloaded separately and are not embedded in this package.
"@
    if ($Rid -eq 'ubuntu-x64') {
        $readme += @"

Supported distribution: Ubuntu 24.04 x64.
Install native desktop and OpenCV dependencies before first launch:
sudo apt install libgeotiff5 liblapack3 libblas3 libopenjp2-7 libgstreamer-plugins-base1.0-0 libavcodec60 libavformat60 libavutil58 libswscale7 libgtk-3-0t64 libfontconfig1 libx11-6 libice6 libsm6 libxext6 libgl1 libegl1 libgbm1 libinput10
"@
    }
    [System.IO.File]::WriteAllText((Join-Path $staging 'RELEASE.txt'), $readme, [System.Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'test-ai-packaging.ps1') -Path $staging
    if ($LASTEXITCODE -ne 0) { throw "$Name package contains optional AI payload." }

    if ($Rid -eq 'win-x64') {
        $archive = Join-Path $releaseRoot "$packageName.zip"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal
    }
    else {
        $archive = Join-Path $releaseRoot "$packageName.tar.gz"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        [VisionStudioTarPackage]::Create($staging, $archive)
    }

    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$archive.sha256", "$hash  $([System.IO.Path]::GetFileName($archive))`n", [System.Text.UTF8Encoding]::new($false))
    Write-Output "$Name package: $archive"
    Write-Output "SHA256: $hash"
}

if ($Platform -in @('Windows', 'All')) { Publish-Platform 'Windows x64' 'win-x64' }
if ($Platform -in @('Linux', 'All')) { Publish-Platform 'Ubuntu Linux x64' 'ubuntu-x64' }
