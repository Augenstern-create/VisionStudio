$ErrorActionPreference = 'Stop'

$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$program = Get-Content (Join-Path $workspaceRoot 'src\VisionStudio.App\Program.cs') -Raw
$project = Get-Content (Join-Path $workspaceRoot 'src\VisionStudio.App\VisionStudio.App.csproj') -Raw
$appXaml = Get-Content (Join-Path $workspaceRoot 'src\VisionStudio.App\App.axaml') -Raw

if ($program -notmatch '\.WithInterFont\(\)') {
    throw 'Avalonia is not configured to load the Inter font.'
}
if ($project -notmatch 'Avalonia\.Fonts\.Inter') {
    throw 'The Inter font package reference is missing.'
}
if ($appXaml -notmatch 'FontFamily" Value="avares://Avalonia\.Fonts\.Inter/Assets#Inter"') {
    throw 'The global Window style does not use Inter.'
}
if ($appXaml -notmatch 'FontSize" Value="13"') {
    throw 'The global base font size is missing.'
}

Write-Output 'Typography checks passed: font package, loader, family and base size.'
