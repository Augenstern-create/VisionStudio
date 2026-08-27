$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$window = Get-Content (Join-Path $workspaceRoot 'src\VisionStudio.App\MainWindow.axaml') -Raw
$mainCode = (Get-ChildItem (Join-Path $workspaceRoot 'src\VisionStudio.App') -Filter 'MainWindow*.cs' |
    Sort-Object Name | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
$store = Get-Content (Join-Path $workspaceRoot 'src\VisionStudio.Ai\AiModelProfileStore.cs') -Raw
$settings = Join-Path $workspaceRoot 'src\VisionStudio.App\SettingsWindow.cs'
$chatControl = Join-Path $workspaceRoot 'src\VisionStudio.App\ChatMessageControl.cs'
$standard = Join-Path $workspaceRoot 'src\VisionStudio.App\config\standard-models.json'

foreach ($name in @('SettingsMenuItem', 'OperatorsActivityButton', 'AiActivityButton', 'OperatorSidebar', 'AiSidebar', 'AiModelComboBox', 'AiImageReferencesPanel', 'AiConversationPanel')) {
    if ($window -notmatch ('x:Name="' + $name + '"')) { throw "AI workbench element is missing: $name" }
}
foreach ($resource in @('ActivityBarBrush', 'SideBarHeaderBrush')) {
    if ($window -notmatch $resource) { throw "VS Code activity bar resource is not used: $resource" }
}
if ($window -notmatch '<PathIcon Width="24" Height="24"' -or $window -match 'Content="[▦✦⚙]"') {
    throw 'Activity bar must use consistent vector icons instead of font glyph content.'
}
if (!(Test-Path -LiteralPath $settings)) { throw 'Settings window is missing.' }
if (!(Test-Path -LiteralPath $chatControl)) { throw 'Dedicated Avalonia chat message control is missing.' }
if (!(Test-Path -LiteralPath $standard)) { throw 'Standard model catalog is missing.' }
$settingsCode = Get-Content $settings -Raw
if ($settingsCode -notmatch 'LoadRemoteEditor\(\);' -or $settingsCode -notmatch 'SaveRemoteEditor\(validate: false\)') {
    throw 'Remote model editor does not reload the selected profile or preserve edits while switching models.'
}
if ($store -notmatch 'AppContext\.BaseDirectory, "config"' -or $store -match 'SpecialFolder\.LocalApplicationData') {
    throw 'AI configuration is not anchored to the software installation config directory.'
}
if ($mainCode -notmatch 'AiContextKey' -or $mainCode -notmatch 'AiModelComboBox_SelectionChanged') {
    throw 'Per-model conversation context switching is not implemented.'
}
if ($store -notmatch 'ai-local-models\.json' -or $store -notmatch 'ai-remote-models\.json' -or $store -notmatch 'EncryptedRemoteAiModelProfile') {
    throw 'Local/remote model storage separation or remote secret encryption is missing.'
}
if ($mainCode -notmatch 'AiImageReferenceCatalog' -or $mainCode -notmatch 'AiPrompt_Drop') {
    throw 'Image-node numbering and drag-to-prompt support is missing.'
}
if (Test-Path -LiteralPath (Join-Path $workspaceRoot 'src\VisionStudio.App\AiAssistantWindow.cs')) {
    throw 'The obsolete standalone AI assistant window still exists.'
}
$chatCode = Get-Content $chatControl -Raw
foreach ($feature in @('SelectableTextBlock', 'QuoteRequested', 'CopyToClipboardAsync', 'DataFormat.Text')) {
    if ($chatCode -notmatch [regex]::Escape($feature)) { throw "Chat message feature is missing: $feature" }
}
if ($mainCode -notmatch 'new ChatMessageControl' -or $mainCode -notmatch 'InsertAiQuote') {
    throw 'AI conversation does not use the selectable chat message control or quote insertion.'
}
if ($mainCode -notmatch 'history\.Concat\(\[new AiChatMessage\("user", text\)\]\)' -or
    $mainCode -notmatch 'new AiChatMessage\("assistant", error\)') {
    throw 'AI send/error turns are not both persisted as distinguishable chat messages.'
}

Write-Output 'AI UI checks passed: model settings, embedded chat, selectable messages, fenced-code copy, quotes, image references and per-model contexts.'
