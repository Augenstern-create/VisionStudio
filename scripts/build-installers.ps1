[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [ValidateSet('Windows', 'Linux', 'All')]
    [string]$Platform = 'All',
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installerRoot = Join-Path $workspaceRoot 'artifacts\installers'
$toolRoot = Join-Path $workspaceRoot 'artifacts\tools\innosetup'
[System.IO.Directory]::CreateDirectory($installerRoot) | Out-Null

if (!$NoPublish) {
    & (Join-Path $PSScriptRoot 'publish-desktop.ps1') -Platform $Platform -Version $Version
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
}

if ($Platform -in @('Windows', 'All')) {
    $compiler = Join-Path $toolRoot 'ISCC.exe'
    if (!(Test-Path -LiteralPath $compiler)) {
        $toolInstaller = Join-Path $env:TEMP 'visionstudio-innosetup-7.0.2-x64.exe'
        Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/innosetup-7.0.2-x64.exe' -OutFile $toolInstaller
        [System.IO.Directory]::CreateDirectory($toolRoot) | Out-Null
        $process = Start-Process -FilePath $toolInstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$toolRoot" -Wait -PassThru
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw 'Inno Setup tool installation failed.' }
    }
    $sourceDir = Join-Path $workspaceRoot "artifacts\releases\VisionStudio-$Version-win-x64"
    $installerLogo = Join-Path $toolRoot 'visionstudio-installer-logo.png'
    Add-Type -AssemblyName System.Drawing
    $sourceIcon = [System.Drawing.Icon]::new((Join-Path $workspaceRoot 'src\VisionStudio.App\Assets\VisionStudio.ico'), 256, 256)
    try {
        $bitmap = $sourceIcon.ToBitmap()
        try { $bitmap.Save($installerLogo, [System.Drawing.Imaging.ImageFormat]::Png) }
        finally { $bitmap.Dispose() }
    }
    finally { $sourceIcon.Dispose() }
    & $compiler "/DSourceDir=$sourceDir" "/DOutputDir=$installerRoot" "/DAppVersion=$Version" "/DWorkspaceRoot=$workspaceRoot" "/DInstallerLogo=$installerLogo" (Join-Path $workspaceRoot 'installers\windows\VisionStudio.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Windows installer compilation failed.' }
    $windowsInstaller = Join-Path $installerRoot "VisionStudio-$Version-win-x64-setup.exe"
    $hash = (Get-FileHash -LiteralPath $windowsInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$windowsInstaller.sha256", "$hash  $([System.IO.Path]::GetFileName($windowsInstaller))`n", [System.Text.UTF8Encoding]::new($false))
}

if ($Platform -in @('Linux', 'All')) {
    $linuxScript = '/mnt/' + $workspaceRoot.Substring(0, 1).ToLowerInvariant() + $workspaceRoot.Substring(2).Replace('\', '/') + '/scripts/build-linux-deb.sh'
    & wsl.exe bash $linuxScript $Version
    if ($LASTEXITCODE -ne 0) { throw 'Linux Debian package compilation failed.' }
}

Get-ChildItem -LiteralPath $installerRoot -File | Select-Object Name, Length
