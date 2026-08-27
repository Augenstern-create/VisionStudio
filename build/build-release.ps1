[CmdletBinding()]
param(
    [string]$Version = '2.0.0',
    [ValidateSet('Windows', 'Linux', 'All')]
    [string]$Platform = 'Windows',
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$IncludeWeb,
    [switch]$FullValidation
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $workspaceRoot 'VisionStudio.sln'
$testProject = Join-Path $workspaceRoot 'tests\VisionStudio.Tests\VisionStudio.Tests.csproj'
$scripts = Join-Path $workspaceRoot 'scripts'

function Invoke-DotNet([string[]]$Arguments, [string]$FailureMessage) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw $FailureMessage }
}

Push-Location $workspaceRoot
try {
    Invoke-DotNet @('build', $solution, '-c', 'Release', '--nologo', "-p:Version=$Version") 'Release solution build failed.'
    if (!$SkipTests) {
        Invoke-DotNet @('test', $testProject, '-c', 'Release', '--no-build', '--nologo') 'Release unit tests failed.'
        & (Join-Path $scripts 'test-changes.ps1') -Dimension CodeExport,Web -NoBuild
        if ($LASTEXITCODE -ne 0) { throw 'Release code-export or Web validation failed.' }
        if ($FullValidation) {
            & (Join-Path $scripts 'test-ui-workflows.ps1')
            if ($LASTEXITCODE -ne 0) { throw 'Release desktop UI validation failed.' }
        }
    }

    if ($SkipInstaller) {
        & (Join-Path $scripts 'publish-desktop.ps1') -Platform $Platform -Version $Version
        if ($LASTEXITCODE -ne 0) { throw 'Release desktop publish failed.' }
    }
    else {
        & (Join-Path $scripts 'build-installers.ps1') -Platform $Platform -Version $Version
        if ($LASTEXITCODE -ne 0) { throw 'Release installer build failed.' }
        if ($FullValidation -and $Platform -in @('Windows', 'All')) {
            & (Join-Path $scripts 'test-windows-installer.ps1')
            if ($LASTEXITCODE -ne 0) { throw 'Windows installer validation failed.' }
        }
        if ($FullValidation -and $Platform -in @('Linux', 'All')) {
            $linuxTest = '/mnt/' + $workspaceRoot.Substring(0, 1).ToLowerInvariant() + $workspaceRoot.Substring(2).Replace('\', '/') + '/scripts/test-linux-installer.sh'
            & wsl.exe bash $linuxTest
            if ($LASTEXITCODE -ne 0) { throw 'Linux installer validation failed.' }
        }
    }

    if ($IncludeWeb) {
        & (Join-Path $scripts 'publish-web.ps1') -Version $Version
        if ($LASTEXITCODE -ne 0) { throw 'Release Web package failed.' }
    }

    Write-Output "Release build completed: version=$Version; platform=$Platform"
    Write-Output "Release packages: $(Join-Path $workspaceRoot 'artifacts\releases')"
    if (!$SkipInstaller) { Write-Output "Installers: $(Join-Path $workspaceRoot 'artifacts\installers')" }
    if ($IncludeWeb) { Write-Output "Web package: $(Join-Path $workspaceRoot 'artifacts\web')" }
}
finally {
    Pop-Location
}
