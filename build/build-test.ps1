[CmdletBinding()]
param(
    [ValidateSet('Windows', 'Linux', 'All')]
    [string]$Platform = 'Windows',
    [string]$Version = '',
    [switch]$SkipTests,
    [switch]$IncludeWeb,
    [switch]$UiAutomation
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $workspaceRoot 'VisionStudio.sln'
$testProject = Join-Path $workspaceRoot 'tests\VisionStudio.Tests\VisionStudio.Tests.csproj'
$appProject = Join-Path $workspaceRoot 'src\VisionStudio.App\VisionStudio.App.csproj'
$cliProject = Join-Path $workspaceRoot 'src\VisionStudio.Cli\VisionStudio.Cli.csproj'
$runtimeProject = Join-Path $workspaceRoot 'src\VisionStudio.Runtimes.App\VisionStudio.Runtimes.App.csproj'
$webProject = Join-Path $workspaceRoot 'src\VisionStudio.Web\VisionStudio.Web.csproj'
$outputRoot = Join-Path $workspaceRoot 'artifacts\builds\test'

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = '2.0.0-preview.' + (Get-Date -Format 'yyyyMMddHHmm')
}
[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null

function Invoke-DotNet([string[]]$Arguments, [string]$FailureMessage) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw $FailureMessage }
}

function Publish-TestBuild([string]$Rid) {
    $destination = Join-Path $outputRoot "VisionStudio-$Version-$Rid"
    [System.IO.Directory]::CreateDirectory($destination) | Out-Null
    Invoke-DotNet @('publish', $appProject, '-c', 'Debug', '-r', $Rid, '--self-contained', 'true', '--nologo',
        '-p:PublishTrimmed=false', "-p:Version=$Version", '-o', $destination) "Desktop test publish failed for $Rid."
    Invoke-DotNet @('publish', $cliProject, '-c', 'Debug', '-r', $Rid, '--self-contained', 'true', '--nologo',
        '-p:PublishTrimmed=false', "-p:Version=$Version", '-o', $destination) "CLI test publish failed for $Rid."
    Invoke-DotNet @('publish', $runtimeProject, '-c', 'Debug', '-r', $Rid, '--self-contained', 'true', '--nologo',
        '-p:PublishTrimmed=false', "-p:Version=$Version", '-o', $destination) "Runtime test publish failed for $Rid."
    Write-Output "Test build: $destination"
}

Push-Location $workspaceRoot
try {
    Invoke-DotNet @('build', $solution, '-c', 'Debug', '--nologo') 'Debug solution build failed.'
    if (!$SkipTests) {
        Invoke-DotNet @('test', $testProject, '-c', 'Debug', '--no-build', '--nologo') 'Debug unit tests failed.'
        & (Join-Path $workspaceRoot 'scripts\test-csharp-export.ps1') -TargetNodeId 'metrics'
        if ($LASTEXITCODE -ne 0) { throw 'Independent C# export test failed for metrics.' }
        & (Join-Path $workspaceRoot 'scripts\test-csharp-export.ps1') -TargetNodeId 'metrics_2' -NoBuild -ExpectUnsupported
        if ($LASTEXITCODE -ne 0) { throw 'RuntimeFrame C# export boundary test failed for metrics_2.' }
        if ($UiAutomation) {
            & (Join-Path $workspaceRoot 'scripts\test-ui-workflows.ps1')
            if ($LASTEXITCODE -ne 0) { throw 'Desktop UI automation failed.' }
        }
    }

    if ($Platform -in @('Windows', 'All')) { Publish-TestBuild 'win-x64' }
    if ($Platform -in @('Linux', 'All')) { Publish-TestBuild 'ubuntu-x64' }
    if ($IncludeWeb) {
        $webDestination = Join-Path $outputRoot "VisionStudio.Web-$Version-ubuntu-x64"
        Invoke-DotNet @('publish', $webProject, '-c', 'Debug', '-r', 'ubuntu-x64', '--self-contained', 'true', '--nologo',
            '-p:PublishTrimmed=false', "-p:Version=$Version", '-o', $webDestination) 'Web test publish failed.'
        Write-Output "Web test build: $webDestination"
    }

    Write-Output "Test build completed: version=$Version; platform=$Platform; output=$outputRoot"
}
finally {
    Pop-Location
}
