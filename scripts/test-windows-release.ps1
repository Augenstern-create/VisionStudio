[CmdletBinding()]
param(
    [string]$Archive
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Archive)) {
    $Archive = Join-Path $workspaceRoot 'artifacts\releases\VisionStudio-2.0.0-win-x64.zip'
}
$Archive = [System.IO.Path]::GetFullPath($Archive)
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path $tempRoot ('visionstudio-win-release-' + [guid]::NewGuid().ToString('N'))
$gui = $null
$runtimeGui = $null

try {
    [System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Expand-Archive -LiteralPath $Archive -DestinationPath $testRoot
    if (!(Test-Path -LiteralPath (Join-Path $testRoot 'VisionStudio.Runtimes.App.exe'))) {
        throw 'Published Runtime executable is missing.'
    }

    $projectRoot = Join-Path $testRoot 'project'
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'samples\DemoProject') -Destination $projectRoot -Recurse
    [System.IO.Directory]::CreateDirectory((Join-Path $projectRoot 'images\input')) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $projectRoot 'images\output')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'tests\image\1.png') -Destination (Join-Path $projectRoot 'images\input\1.png')

    $cli = Join-Path $testRoot 'VisionStudio.Cli.exe'
    & $cli validate (Join-Path $projectRoot 'workflows\main.vflow')
    if ($LASTEXITCODE -ne 0) { throw 'Published CLI validation failed.' }
    $result = Join-Path $projectRoot 'images\output\windows-result.png'
    & $cli run (Join-Path $projectRoot 'workflows\main.vflow') $result 'metrics'
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $result)) { throw 'Published OpenCV workflow failed.' }

    $gui = Start-Process -FilePath (Join-Path $testRoot 'VisionStudio.exe') -PassThru
    Start-Sleep -Seconds 5
    $gui.Refresh()
    if ($gui.HasExited) { throw "Published GUI exited unexpectedly with code $($gui.ExitCode)." }
    $runtimeGui = Start-Process -FilePath (Join-Path $testRoot 'VisionStudio.Runtimes.App.exe') `
        -ArgumentList ('"' + (Join-Path $projectRoot 'DemoProject.vstudio') + '"') -PassThru
    Start-Sleep -Seconds 5
    $runtimeGui.Refresh()
    if ($runtimeGui.HasExited) { throw "Published Runtime exited unexpectedly with code $($runtimeGui.ExitCode)." }
    Write-Output 'Windows release passed: editor/Runtime startup, CLI validation and native OpenCV execution.'
}
finally {
    if ($null -ne $gui -and !$gui.HasExited) {
        Stop-Process -Id $gui.Id
        $gui.WaitForExit(5000) | Out-Null
    }
    if ($null -ne $runtimeGui -and !$runtimeGui.HasExited) {
        Stop-Process -Id $runtimeGui.Id
        $runtimeGui.WaitForExit(5000) | Out-Null
    }
    $resolved = [System.IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolved).StartsWith('visionstudio-win-release-', [StringComparison]::Ordinal)) {
        for ($attempt = 0; $attempt -lt 5 -and (Test-Path -LiteralPath $resolved); $attempt++) {
            try { Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop }
            catch {
                if ($attempt -eq 4) { throw }
                Start-Sleep -Milliseconds 300
            }
        }
    }
}
