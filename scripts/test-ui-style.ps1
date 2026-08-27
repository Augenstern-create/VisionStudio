$ErrorActionPreference = 'Stop'

$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appXaml = Get-Content (Join-Path $workspaceRoot 'src\VisionStudio.App\App.axaml') -Raw
$windowXaml = Get-Content (Join-Path $workspaceRoot 'src\VisionStudio.App\MainWindow.axaml') -Raw
$themeManager = Get-Content (Join-Path $workspaceRoot 'src\VisionStudio.App\UiThemeManager.cs') -Raw
$iconPath = Join-Path $workspaceRoot 'src\VisionStudio.App\Assets\VisionStudio.ico'

$requiredBrushes = @(
    'AppBackgroundBrush', 'WorkbenchBrush', 'SideBarBrush', 'PanelBrush',
    'TitleBarBrush', 'BorderBrush', 'PrimaryBrush', 'ActivityBarBorderBrush',
    'ActivitySelectionBrush', 'TextBrush',
    'MutedTextBrush', 'StatusBarBrush'
)
foreach ($brush in $requiredBrushes) {
    if ($appXaml -notmatch [regex]::Escape('x:Key="' + $brush + '"')) {
        throw "Missing required theme resource: $brush"
    }
}

foreach ($theme in @('Dark', 'Light', 'Gradient')) {
    if ($themeManager -notmatch ('UiThemeKind\.' + $theme)) {
        throw "Theme manager does not implement $theme."
    }
    if ($windowXaml -notmatch ('Tag="' + $theme + '"')) {
        throw "View menu does not expose $theme."
    }
}

if (($windowXaml | Select-String 'PointerPressed="TitleBar_PointerPressed"' -AllMatches).Matches.Count -ne 2) {
    throw 'Both welcome and editor title bars must expose window dragging.'
}
if (($windowXaml | Select-String 'WindowDecorationProperties.ElementRole="TitleBar"' -AllMatches).Matches.Count -ne 2) {
    throw 'Both title bars must be registered as native window drag regions.'
}
if (($windowXaml | Select-String 'WindowDecorationProperties.ElementRole="User"' -AllMatches).Matches.Count -lt 3) {
    throw 'Interactive title-bar controls must opt out of native dragging.'
}
if ($windowXaml -notmatch 'BorderBrush="\{DynamicResource ActivityBarBorderBrush\}"') {
    throw 'Activity bar divider is not bound to its theme resource.'
}
if ($appXaml -notmatch 'BorderBrush" Value="\{DynamicResource ActivitySelectionBrush\}"') {
    throw 'Activity selection border is not bound to its theme resource.'
}
foreach ($color in @('#007ACC', '#0067B8', '#6C63FF')) {
    if ($themeManager -notmatch [regex]::Escape('("ActivitySelectionBrush", "' + $color + '")')) {
        throw "Theme manager is missing activity selection color $color."
    }
}

if ($windowXaml -notmatch 'Icon="/Assets/VisionStudio.ico"') {
    throw 'Main window does not reference the application icon.'
}
if (!(Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw 'Application icon file is missing.'
}

$bytes = [System.IO.File]::ReadAllBytes($iconPath)
if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) {
    throw 'Application icon has an invalid ICO header.'
}
$iconCount = [BitConverter]::ToUInt16($bytes, 4)
if ($iconCount -lt 5) {
    throw "Application icon should contain multiple Windows sizes; found $iconCount."
}

Write-Output "UI style checks passed: theme resources, three theme choices and $iconCount icon sizes."
