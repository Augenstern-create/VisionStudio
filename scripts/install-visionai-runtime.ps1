param(
    [string]$PackagePath,
    [string]$Destination,
    [switch]$IncludeCuda
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $localPackage = Join-Path $repositoryRoot 'visionai\VisionAI-0.1.0-windows-x64.zip'
    $siblingPackage = Join-Path (Split-Path -Parent $repositoryRoot) 'VisionAI\out\packages\VisionAI-0.1.0-windows-x64.zip'
    $PackagePath = if (Test-Path -LiteralPath $localPackage) { $localPackage } else { $siblingPackage }
}
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repositoryRoot 'plugins\VisionAI'
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$Destination = [IO.Path]::GetFullPath($Destination)
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "VisionAI package was not found: $PackagePath"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Directory]::CreateDirectory((Join-Path $Destination 'bin')) | Out-Null
$required = @('vision_ai.dll', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll')
if ($IncludeCuda) { $required += 'onnxruntime_providers_cuda.dll' }
$archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    foreach ($fileName in $required) {
        $entry = $archive.Entries | Where-Object { $_.Name -eq $fileName -and $_.FullName -match '[\\/]bin[\\/]' } | Select-Object -First 1
        if ($null -eq $entry) { throw "Package does not contain bin/$fileName" }
        $target = Join-Path (Join-Path $Destination 'bin') $fileName
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
}
finally {
    $archive.Dispose()
}

$manifest = [ordered]@{
    schemaVersion = 1
    package = [IO.Path]::GetFileName($PackagePath)
    installedUtc = [DateTime]::UtcNow.ToString('O')
    cudaIncluded = [bool]$IncludeCuda
    runtimeDirectory = 'bin'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'visionai-runtime.json') -Encoding utf8
Write-Host "VisionAI runtime installed to: $Destination"
