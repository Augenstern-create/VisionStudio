[CmdletBinding()]
param([string]$Version = '2.0.0')

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Require-Text([string]$Path, [string]$Text) {
    $fullPath = Join-Path $workspaceRoot $Path
    if (!(Test-Path -LiteralPath $fullPath)) { throw "Required release file is missing: $Path" }
    if (!(Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8).Contains($Text)) {
        throw "Release metadata '$Path' does not contain '$Text'."
    }
}

Require-Text 'Directory.Build.props' "<Version>$Version</Version>"
Require-Text 'plugins\VisionStudio.Ai.LlamaCpp\plugin.json' ('"version": "' + $Version + '"')
Require-Text 'src\VisionStudio.Web\wwwroot\index.html' $Version
Require-Text 'README.md' '-Version 2.0.0'
Require-Text 'docs\boundaries.md' 'V2.0 明确不发布 Linux 桌面或 Android/iOS 版本'
Require-Text 'docs\platform-release.md' 'VisionStudio.Runtimes.App'
Require-Text 'samples\DemoProject\DemoProject.vstudio' 'Vision Studio Demo Project'
Require-Text 'README.md' 'V2.0 正式发布 Windows x64 桌面版和 Ubuntu Server Web'
Require-Text 'README.md' '预制模型一键下载暂不作为 V2.0 稳定能力'

Write-Output "V2.0 release metadata passed: version=$Version; platforms=Windows/Web; Runtime=Windows main package; local models=manual configuration."
