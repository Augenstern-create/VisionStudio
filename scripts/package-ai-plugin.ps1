[CmdletBinding()]
param(
    [string]$Version = '2.0.0'
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $workspaceRoot 'plugins\VisionStudio.Ai.LlamaCpp\VisionStudio.Ai.LlamaCpp.csproj'
$output = Join-Path $workspaceRoot "artifacts\plugins\VisionStudio.Ai.LlamaCpp-$Version"
$archive = "$output.zip"

& dotnet publish $project -c Release --nologo -p:Version=$Version -o $output
if ($LASTEXITCODE -ne 0) { throw 'AI connector plugin publish failed.' }

# Shared host contracts are supplied by Vision Studio and must not be duplicated by a plugin package.
@('VisionStudio.Core.dll', 'VisionStudio.Ai.Abstractions.dll') | ForEach-Object {
    $path = Join-Path $output $_
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
& (Join-Path $PSScriptRoot 'test-ai-packaging.ps1') -Path $output -AllowProviderPlugin
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Output "AI connector plugin: $archive"
