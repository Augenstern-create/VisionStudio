[CmdletBinding()]
param(
    [ValidateSet('Windows', 'Linux', 'All')]
    [string]$Platform = 'All',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '2.0.0'
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimeProject = Join-Path $workspaceRoot 'src\VisionStudio.Runtimes.App\VisionStudio.Runtimes.App.csproj'
$outputRoot = Join-Path $workspaceRoot "artifacts\runtimes\$($Configuration.ToLowerInvariant())"
[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null

function Publish-Runtime([string]$Rid) {
    $destination = Join-Path $outputRoot "VisionStudio.Runtime-$Version-$Rid"
    $fullDestination = [System.IO.Path]::GetFullPath($destination)
    $safePrefix = [System.IO.Path]::GetFullPath($outputRoot).TrimEnd('\') + '\'
    if (!$fullDestination.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        ![System.IO.Path]::GetFileName($fullDestination).StartsWith('VisionStudio.Runtime-', [StringComparison]::Ordinal)) {
        throw "Refusing to replace unsafe runtime output: $fullDestination"
    }
    if (Test-Path -LiteralPath $fullDestination) {
        Remove-Item -LiteralPath $fullDestination -Recurse -Force
    }
    & dotnet publish $runtimeProject -c $Configuration -r $Rid --self-contained true --nologo `
        -p:Version=$Version -p:PublishTrimmed=false -o $fullDestination
    if ($LASTEXITCODE -ne 0) { throw "Runtime publish failed for $Rid." }
    Write-Output "C# runtime: $fullDestination"
}

if ($Platform -in @('Windows', 'All')) { Publish-Runtime 'win-x64' }
if ($Platform -in @('Linux', 'All')) { Publish-Runtime 'ubuntu-x64' }
