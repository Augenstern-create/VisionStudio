[CmdletBinding()]
param(
    [string]$Installer
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Installer)) {
    $Installer = Join-Path $workspaceRoot 'artifacts\installers\VisionStudio-2.0.0-win-x64-setup.exe'
}
$Installer = [System.IO.Path]::GetFullPath($Installer)
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path $tempRoot ('visionstudio-installer-test-' + [guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot 'app'
$gui = $null
$runtimeGui = $null

try {
    [System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $projectRoot = Join-Path $testRoot 'project'
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'samples\DemoProject') -Destination $projectRoot -Recurse
    [System.IO.Directory]::CreateDirectory((Join-Path $projectRoot 'images\input')) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $projectRoot 'images\output')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'tests\image\1.png') -Destination (Join-Path $projectRoot 'images\input\1.png')

    $setup = Start-Process -FilePath $Installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/TESTINSTALL=1', ("/DIR=$installRoot") -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw "Installer exited with $($setup.ExitCode)." }
    $cli = Join-Path $installRoot 'VisionStudio.Cli.exe'
    $runtime = Join-Path $installRoot 'VisionStudio.Runtimes.App.exe'
    if (!(Test-Path -LiteralPath $runtime)) { throw 'Installed Runtime executable is missing.' }
    & $cli validate (Join-Path $projectRoot 'workflows\main.vflow')
    if ($LASTEXITCODE -ne 0) { throw 'Installed CLI validation failed.' }
    & $cli run (Join-Path $projectRoot 'workflows\main.vflow') (Join-Path $projectRoot 'images\output\installer-result.png') 'metrics'
    if ($LASTEXITCODE -ne 0) { throw 'Installed OpenCV run failed.' }

    $gui = Start-Process -FilePath (Join-Path $installRoot 'VisionStudio.exe') -PassThru
    Start-Sleep -Seconds 5
    $gui.Refresh()
    if ($gui.HasExited) { throw "Installed GUI exited with $($gui.ExitCode)." }
    Stop-Process -Id $gui.Id
    $gui.WaitForExit(5000) | Out-Null

    $runtimeGui = Start-Process -FilePath $runtime -ArgumentList ('"' + (Join-Path $projectRoot 'DemoProject.vstudio') + '"') -PassThru
    Start-Sleep -Seconds 5
    $runtimeGui.Refresh()
    if ($runtimeGui.HasExited) { throw "Installed Runtime exited with $($runtimeGui.ExitCode)." }
    Stop-Process -Id $runtimeGui.Id
    $runtimeGui.WaitForExit(5000) | Out-Null

    $uninstall = Start-Process -FilePath (Join-Path $installRoot 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Uninstaller exited with $($uninstall.ExitCode)." }
    Write-Output 'Windows installer passed: install, editor/Runtime startup, CLI validation, native OpenCV run and uninstall.'
}
finally {
    if ($null -ne $gui -and !$gui.HasExited) { Stop-Process -Id $gui.Id }
    if ($null -ne $runtimeGui -and !$runtimeGui.HasExited) { Stop-Process -Id $runtimeGui.Id }
    $resolved = [System.IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolved).StartsWith('visionstudio-installer-test-', [StringComparison]::Ordinal)) {
        for ($attempt = 0; $attempt -lt 5 -and (Test-Path -LiteralPath $resolved); $attempt++) {
            try { Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop }
            catch {
                if ($attempt -eq 4) { throw }
                Start-Sleep -Milliseconds 300
            }
        }
    }
}
