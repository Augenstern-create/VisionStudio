$ErrorActionPreference = 'Stop'

$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryBase ('VisionStudioUi-' + [Guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $testRoot '自动创建的项目文件夹'
$solution = Join-Path $workspaceRoot 'VisionStudio.sln'
$cliDll = Join-Path $workspaceRoot 'src\VisionStudio.Cli\bin\Release\net9.0\VisionStudio.Cli.dll'
$appExe = Join-Path $workspaceRoot 'src\VisionStudio.App\bin\Release\net9.0\VisionStudio.exe'
$appProcess = $null
$fakeAiJob = $null
$previousDisableOpenFolder = $env:VISIONSTUDIO_DISABLE_OPEN_FOLDER
$previousUiLanguage = $env:VISIONSTUDIO_UI_LANGUAGE
$previousUiTheme = $env:VISIONSTUDIO_UI_THEME
$previousTestMode = $env:VISIONSTUDIO_TEST_MODE
$previousTestConfig = $env:VISIONSTUDIO_TEST_CONFIG_DIRECTORY
$previousTestEndpoint = $env:VISIONSTUDIO_AI_TEST_ENDPOINT
$env:VISIONSTUDIO_DISABLE_OPEN_FOLDER = '1'
$env:VISIONSTUDIO_UI_LANGUAGE = 'zh-CN'
$env:VISIONSTUDIO_UI_THEME = 'Dark'
$env:VISIONSTUDIO_TEST_MODE = '1'
$env:VISIONSTUDIO_TEST_CONFIG_DIRECTORY = Join-Path $testRoot 'isolated-config'

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet command failed: $($Arguments -join ' ')" }
}

function Find-ElementByName($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-VisibleElementByName($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) |
                Where-Object { !$_.Current.IsOffscreen } | Select-Object -First 1
        }
        catch [System.Runtime.InteropServices.COMException] {
            if ($attempt -eq 4) { throw }
            Start-Sleep -Milliseconds 150
        }
    }
}

function Find-VisibleElementContainingName($Root, [string]$Text) {
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { !$_.Current.IsOffscreen -and $_.Current.Name.Contains($Text, [System.StringComparison]::CurrentCulture) } |
        Select-Object -First 1
}

function Find-GraphNode($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $matches = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($match in $matches) {
        if ($match.Current.AutomationId.StartsWith('node-', [System.StringComparison]::Ordinal)) { return $match }
    }
    return $null
}

function Invoke-Element($Element, [string]$Label = 'element') {
    if ($null -eq $Element) { throw 'Expected automation element was not found.' }
    Write-Output "Invoking UI element: $Label"
    $pattern = [System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Click-AutomationElement($Element, [string]$Label = 'element') {
    if ($null -eq $Element) { throw "Expected automation element was not found: $Label" }
    $bounds = $Element.Current.BoundingRectangle
    [VisionStudioNativeMouse]::SetCursorPos([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2)) | Out-Null
    [VisionStudioNativeMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [VisionStudioNativeMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function RightClick-ScreenPoint([int]$X, [int]$Y) {
    [VisionStudioNativeMouse]::SetCursorPos($X, $Y) | Out-Null
    [VisionStudioNativeMouse]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
    [VisionStudioNativeMouse]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
}

function Get-FirstConnectionScreenPoint($CanvasElement, [string]$WorkflowPath) {
    $saved = Get-Content -LiteralPath $WorkflowPath -Raw | ConvertFrom-Json
    $edge = $saved.connections | Select-Object -First 1
    if ($null -eq $edge) { throw 'Workflow has no connection for interaction testing.' }
    $source = $saved.nodes | Where-Object { $_.id -eq $edge.fromNode } | Select-Object -First 1
    if ($null -eq $source) { throw 'Connection source node is missing.' }
    $bounds = $CanvasElement.Current.BoundingRectangle
    $scale = [VisionStudioNativeMouse]::GetDpiForWindow($script:windowHandle) / 96.0
    return [pscustomobject]@{
        X = [int]($bounds.Left + ([double]$source.x + 184 + 12) * $scale)
        Y = [int]($bounds.Top + ([double]$source.y + 56) * $scale)
    }
}

function Open-FirstConnectionContextMenu($Window, $CanvasElement, [string]$WorkflowPath) {
    # Port zero is at node Y + HeaderHeight(36) + body offset(18). Clicking
    # twelve pixels after the output port lands on the first horizontal segment.
    $point = Get-FirstConnectionScreenPoint $CanvasElement $WorkflowPath
    [VisionStudioNativeMouse]::SetForegroundWindow($script:windowHandle) | Out-Null
    RightClick-ScreenPoint $point.X $point.Y
    Start-Sleep -Milliseconds 350
    $item = Find-VisibleElementByName ([System.Windows.Automation.AutomationElement]::RootElement) '选择后续节点'
    if ($null -eq $item) { throw "Connection context menu did not open at screen point $($point.X),$($point.Y)." }
    return $item
}

function Invoke-MenuCommand($Window, [string]$MenuName, [string]$CommandName, [string]$Label) {
    [VisionStudioNativeMouse]::SetForegroundWindow($script:windowHandle) | Out-Null
    Click-AutomationElement (Find-VisibleElementByName $Window $MenuName) "$MenuName menu"
    Start-Sleep -Milliseconds 250
    $command = Find-VisibleElementByName ([System.Windows.Automation.AutomationElement]::RootElement) $CommandName
    Click-AutomationElement $command $Label
    Start-Sleep -Milliseconds 250
}

function Invoke-NestedMenuCommand($Window, [string]$MenuName, [string]$SubmenuName, [string]$CommandName, [string]$Label) {
    [VisionStudioNativeMouse]::SetForegroundWindow($script:windowHandle) | Out-Null
    Click-AutomationElement (Find-VisibleElementByName $Window $MenuName) "$MenuName menu"
    Start-Sleep -Milliseconds 250
    Click-AutomationElement (Find-VisibleElementByName ([System.Windows.Automation.AutomationElement]::RootElement) $SubmenuName) "$SubmenuName submenu"
    Start-Sleep -Milliseconds 250
    Click-AutomationElement (Find-VisibleElementByName ([System.Windows.Automation.AutomationElement]::RootElement) $CommandName) $Label
    Start-Sleep -Milliseconds 350
}

function Click-ScreenPoint([int]$X, [int]$Y, [bool]$RightButton = $false) {
    $point = [VisionStudioNativeMouse+POINT]::new()
    $point.X = $X; $point.Y = $Y
    [VisionStudioNativeMouse]::ScreenToClient($script:windowHandle, [ref]$point) | Out-Null
    $lParam = [IntPtr](($point.Y -shl 16) -bor ($point.X -band 0xffff))
    if ($RightButton) {
        [VisionStudioNativeMouse]::SendMessage($script:windowHandle, 0x0204, [IntPtr]2, $lParam) | Out-Null
        [VisionStudioNativeMouse]::SendMessage($script:windowHandle, 0x0205, [IntPtr]0, $lParam) | Out-Null
    } else {
        [VisionStudioNativeMouse]::SendMessage($script:windowHandle, 0x0201, [IntPtr]1, $lParam) | Out-Null
        [VisionStudioNativeMouse]::SendMessage($script:windowHandle, 0x0202, [IntPtr]0, $lParam) | Out-Null
    }
}

function Drag-ScreenPoint([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY) {
    $from = [VisionStudioNativeMouse+POINT]::new(); $from.X = $FromX; $from.Y = $FromY
    $to = [VisionStudioNativeMouse+POINT]::new(); $to.X = $ToX; $to.Y = $ToY
    [VisionStudioNativeMouse]::ScreenToClient($script:windowHandle, [ref]$from) | Out-Null
    [VisionStudioNativeMouse]::ScreenToClient($script:windowHandle, [ref]$to) | Out-Null
    $fromParam = [IntPtr](($from.Y -shl 16) -bor ($from.X -band 0xffff))
    $toParam = [IntPtr](($to.Y -shl 16) -bor ($to.X -band 0xffff))
    [VisionStudioNativeMouse]::SendMessage($script:windowHandle, 0x0201, [IntPtr]1, $fromParam) | Out-Null
    Start-Sleep -Milliseconds 150
    [VisionStudioNativeMouse]::SendMessage($script:windowHandle, 0x0200, [IntPtr]1, $toParam) | Out-Null
    Start-Sleep -Milliseconds 150
    [VisionStudioNativeMouse]::SendMessage($script:windowHandle, 0x0202, [IntPtr]0, $toParam) | Out-Null
}

function Drag-WindowTitleBar([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY) {
    [VisionStudioNativeMouse]::SetForegroundWindow($script:windowHandle) | Out-Null
    Start-Sleep -Milliseconds 180
    [VisionStudioNativeMouse]::SetCursorPos($FromX, $FromY) | Out-Null
    [VisionStudioNativeMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 180
    for ($step = 1; $step -le 8; $step++) {
        $x = [int]($FromX + ($ToX - $FromX) * $step / 8)
        $y = [int]($FromY + ($ToY - $FromY) * $step / 8)
        [VisionStudioNativeMouse]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 45
    }
    [VisionStudioNativeMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Assert-ScreenColorNear($Element, [string]$ExpectedHex, [string]$Label) {
    if ($null -eq $Element) { throw "Expected automation element was not found: $Label" }
    $bounds = $Element.Current.BoundingRectangle
    $expected = [System.Drawing.ColorTranslator]::FromHtml($ExpectedHex)
    $left = [int][Math]::Floor($bounds.Left)
    $top = [int][Math]::Floor($bounds.Top + 8)
    $height = [Math]::Max(1, [int][Math]::Floor($bounds.Height - 16))
    $bitmap = [System.Drawing.Bitmap]::new(6, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size)
        for ($x = 0; $x -lt $bitmap.Width; $x++) {
            for ($y = 0; $y -lt $bitmap.Height; $y++) {
                $actual = $bitmap.GetPixel($x, $y)
                if ([Math]::Abs([int]$actual.R - [int]$expected.R) -le 8 -and
                    [Math]::Abs([int]$actual.G - [int]$expected.G) -le 8 -and
                    [Math]::Abs([int]$actual.B - [int]$expected.B) -le 8) {
                    Write-Output "$Label rendered $ExpectedHex."
                    return
                }
            }
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
    throw "$Label did not render expected theme color $ExpectedHex."
}

function Press-Key([byte]$VirtualKey) {
    [VisionStudioNativeMouse]::keybd_event($VirtualKey, 0, 0, [UIntPtr]::Zero)
    [VisionStudioNativeMouse]::keybd_event($VirtualKey, 0, 2, [UIntPtr]::Zero)
}

function Press-CtrlZ {
    [VisionStudioNativeMouse]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    [VisionStudioNativeMouse]::keybd_event(0x5A, 0, 0, [UIntPtr]::Zero)
    [VisionStudioNativeMouse]::keybd_event(0x5A, 0, 2, [UIntPtr]::Zero)
    [VisionStudioNativeMouse]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
}

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $portProbe.Start()
    $fakeAiPort = ([System.Net.IPEndPoint]$portProbe.LocalEndpoint).Port
    $portProbe.Stop()
    $env:VISIONSTUDIO_AI_TEST_ENDPOINT = "http://127.0.0.1:$fakeAiPort"
    $fakeAiJob = Start-Job -ArgumentList $fakeAiPort -ScriptBlock {
        param($Port)
        $listener = [System.Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        $requestCount = 0
        try {
            while ($listener.IsListening) {
                $context = $listener.GetContext()
                $reader = [System.IO.StreamReader]::new($context.Request.InputStream, $context.Request.ContentEncoding)
                $requestBody = $reader.ReadToEnd()
                $reader.Dispose()
                $requestCount++
                $plan = if ($requestCount -le 3) {
                    '{"summary":"故意返回的不完整计划","actions":[]}'
                } else {
                    '{"type":"add_pipeline","fromNode":"model_wrong_source","operatorIds":["Image.Gray","Image.GaussianBlur"],"nodeIds":["ui_gray","ui_blur"]}'
                }
                $payload = @{ choices = @(@{ message = @{ content = $plan } }) } | ConvertTo-Json -Depth 6 -Compress
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
                $context.Response.StatusCode = 200
                $context.Response.ContentType = 'application/json'
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                $context.Response.Close()
            }
        } finally { $listener.Close() }
    }
    Start-Sleep -Milliseconds 500
    Invoke-DotNet @('build', $solution, '-c', 'Release', '--nologo')
    Invoke-DotNet @($cliDll, 'new', $projectRoot, '自动化视觉项目')
    $projectFile = Get-ChildItem -LiteralPath $projectRoot -Filter '*.vstudio' | Select-Object -First 1
    if ($null -eq $projectFile) { throw 'Project creation did not create a .vstudio project file.' }

    foreach ($relativeDirectory in @('workflows', 'images\input', 'images\cache', 'images\output', 'output', 'snapshots', '.project\autosave')) {
        if (!(Test-Path -LiteralPath (Join-Path $projectRoot $relativeDirectory) -PathType Container)) {
            throw "Project creation missed directory: $relativeDirectory"
        }
    }

    $inputPath = Join-Path $projectRoot 'images\input\input.png'
    $onePixelPng = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='
    [System.IO.File]::WriteAllBytes($inputPath, [Convert]::FromBase64String($onePixelPng))
    Invoke-DotNet @($cliDll, 'run', (Join-Path $projectRoot 'workflows\main.vflow'), (Join-Path $projectRoot 'images\output\initial.png'))

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Drawing
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VisionStudioNativeMouse
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, ref RECT rect);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
}
'@

    $quotedProjectPath = '"' + $projectFile.FullName + '"'
    $appStdOut = Join-Path $testRoot 'app-stdout.log'
    $appStdErr = Join-Path $testRoot 'app-stderr.log'
    $appProcess = Start-Process -FilePath $appExe -ArgumentList @($quotedProjectPath) -PassThru -WindowStyle Hidden -RedirectStandardOutput $appStdOut -RedirectStandardError $appStdErr
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 250
        $appProcess.Refresh()
    } while ($appProcess.MainWindowHandle -eq 0 -and !$appProcess.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if ($appProcess.HasExited) { throw "App exited during startup with code $($appProcess.ExitCode)." }
    if ($appProcess.MainWindowHandle -eq 0) { throw 'Main window was not created.' }

    $extensionKey = Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Classes\.vstudio'
    if ($extensionKey.'(default)' -ne 'VisionStudio.Project') { throw 'The .vstudio extension was not associated with Vision Studio.' }
    $openCommand = (Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Classes\VisionStudio.Project\shell\open\command').'(default)'
    if ($openCommand -notlike '*VisionStudio.exe*"%1"*') { throw "Invalid .vstudio open command: $openCommand" }

    $script:windowHandle = $appProcess.MainWindowHandle
    [VisionStudioNativeMouse]::SetForegroundWindow($appProcess.MainWindowHandle) | Out-Null
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)
    $canvas = $null
    $editorDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $canvas = Find-ElementByName $window '算法画布'
        $appProcess.Refresh()
    } while ($null -eq $canvas -and !$appProcess.HasExited -and [DateTime]::UtcNow -lt $editorDeadline)
    if ($null -eq $canvas) {
        $visibleNames = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { ![string]::IsNullOrWhiteSpace($_) }
        throw "Editor canvas did not appear after opening the project. Visible UI: $($visibleNames -join ' | ')"
    }
    if ($null -eq (Find-ElementByName $window '算子：滤波')) { throw 'Collapsed operator category was not exposed.' }
    $canvasRect = $canvas.Current.BoundingRectangle
    $searchBox = Find-ElementByName $window '搜索算子…'
    if ($null -eq $searchBox) { throw 'The title-bar operator search box was not exposed.' }
    $windowRect = $window.Current.BoundingRectangle
    $searchRect = $searchBox.Current.BoundingRectangle
    $windowCenter = $windowRect.Left + $windowRect.Width / 2
    $searchCenter = $searchRect.Left + $searchRect.Width / 2
    if ([Math]::Abs($windowCenter - $searchCenter) -gt 45) { throw 'The operator search box is not centered in the title bar.' }

    $dragBefore = $window.Current.BoundingRectangle
    $titleScale = [VisionStudioNativeMouse]::GetDpiForWindow($script:windowHandle) / 96.0
    # The final title-bar column is 245 DIPs wide. Its rightmost ~145 DIPs are
    # reserved for native caption buttons, so the center of the remaining strip
    # is a deterministic drag target that cannot overlap the centered search box.
    $dragFromX = [int]($dragBefore.Right - 195 * $titleScale)
    $dragFromY = [int]($dragBefore.Top + 22 * $titleScale)
    Drag-WindowTitleBar $dragFromX $dragFromY ($dragFromX + 70) ($dragFromY + 40)
    Start-Sleep -Milliseconds 500
    $dragAfter = $window.Current.BoundingRectangle
    if ([Math]::Abs($dragAfter.Left - $dragBefore.Left) -lt 30 -or [Math]::Abs($dragAfter.Top - $dragBefore.Top) -lt 20) {
        throw "Dragging the editor title bar did not move the window. Before=$($dragBefore.Left),$($dragBefore.Top); After=$($dragAfter.Left),$($dragAfter.Top)."
    }
    Write-Output "Title-bar drag moved the window from $([int]$dragBefore.Left),$([int]$dragBefore.Top) to $([int]$dragAfter.Left),$([int]$dragAfter.Top)."

    Invoke-NestedMenuCommand $window '视图' '主题' '白色主题' 'Switch to light theme'
    Assert-ScreenColorNear (Find-ElementByName $window '▦') '#0067B8' 'Light activity selection border'
    Invoke-NestedMenuCommand $window '视图' '主题' '渐变主题' 'Switch to gradient theme'
    Assert-ScreenColorNear (Find-ElementByName $window '▦') '#6C63FF' 'Gradient activity selection border'
    Invoke-NestedMenuCommand $window '视图' '主题' '黑色主题' 'Restore dark theme'
    Assert-ScreenColorNear (Find-ElementByName $window '▦') '#007ACC' 'Dark activity selection border'
    Invoke-NestedMenuCommand $window '视图' '语言' 'English' 'Switch editor to English'
    if ($null -eq (Find-ElementByName $window 'Search operators…')) { throw 'Editor language menu did not apply English.' }
    Invoke-NestedMenuCommand $window 'View' 'Language' 'Chinese' 'Restore editor to Chinese'
    $appProcess.Refresh()
    if ($appProcess.HasExited) { throw 'App exited while switching themes.' }

    Invoke-MenuCommand $window '帮助' '关于' 'About dialog'
    $aboutDialog = Find-ElementByName ([System.Windows.Automation.AutomationElement]::RootElement) '关于 Vision Studio'
    if ($null -eq $aboutDialog -or $null -eq (Find-ElementByName $aboutDialog '基于 OpenCV、Emgu CV、Avalonia 和 .NET 9')) {
        throw 'About dialog did not expose the software information.'
    }
    Invoke-Element (Find-ElementByName $aboutDialog '关闭') 'Close About dialog'
    Start-Sleep -Milliseconds 300

    Invoke-MenuCommand $window '文件' '设置' 'Settings dialog'
    $settingsDialog = Find-ElementByName ([System.Windows.Automation.AutomationElement]::RootElement) '设置'
    if ($null -eq $settingsDialog -or $null -eq (Find-ElementByName $settingsDialog '本地模型') -or $null -eq (Find-ElementByName $settingsDialog '远程模型')) {
        throw 'Settings dialog did not expose separate local and remote AI model management.'
    }
    Invoke-Element (Find-ElementByName $settingsDialog '取消') 'Close Settings dialog'
    Start-Sleep -Milliseconds 300
    Invoke-Element (Find-VisibleElementByName $window '✦') 'Open embedded AI activity'
    Start-Sleep -Milliseconds 700
    if ($null -eq (Find-VisibleElementByName $window 'AI 助手')) { throw 'Embedded AI sidebar did not appear.' }
    $aiConversation = Find-ElementByName $window 'AI 对话'
    if ($null -eq $aiConversation) { throw 'AI conversation panel was not exposed.' }

    # Send through the real AI text box/button against an isolated local fake
    # OpenAI endpoint. First force an error and verify that the user and error
    # remain two distinct persisted/rendered turns.
    $aiPrompt = Find-VisibleElementByName $window '描述需求，可写 IMG-01、图片编号或节点名称，也可把图像节点编号拖到这里…'
    if ($null -eq $aiPrompt) { throw 'AI prompt text box was not exposed.' }
    $aiPromptValue = [System.Windows.Automation.ValuePattern]$aiPrompt.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $errorPrompt = '触发错误测试：请分析 IMG-01'
    $aiPromptValue.SetValue($errorPrompt)
    Invoke-Element (Find-VisibleElementByName $window '发送') 'Send deterministic AI error turn'
    $contextFile = Join-Path $env:VISIONSTUDIO_TEST_CONFIG_DIRECTORY 'ai-contexts.json'
    $aiDeadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 250
        if (Test-Path -LiteralPath $contextFile) {
            $storedContexts = Get-Content -LiteralPath $contextFile -Raw | ConvertFrom-Json
            $contextProperty = $storedContexts.PSObject.Properties | Select-Object -First 1
            $storedTurns = @($contextProperty.Value)
        }
    } while (($null -eq $storedTurns -or $storedTurns.Count -lt 2) -and [DateTime]::UtcNow -lt $aiDeadline)
    if ($storedTurns.Count -lt 2 -or $storedTurns[-2].role -ne 'user' -or $storedTurns[-1].role -ne 'assistant') {
        throw 'AI context did not persist user then assistant error as separate turns.'
    }
    $visibleUserTurn = Find-ElementByName $aiConversation ("你: " + $storedTurns[-2].content)
    $visibleErrorTurn = Find-ElementByName $aiConversation ("AI 助手: " + $storedTurns[-1].content)
    if ($null -eq $visibleUserTurn -or $null -eq $visibleErrorTurn) { throw 'AI error path did not render distinct user and assistant error bubbles.' }

    # Then return the exact single-action shape from the reported failure and
    # verify the real chat renders host-generated node and parameter details.
    $successPrompt = '为 IMG-01 创建灰度和平滑流程'
    $aiPromptValue.SetValue($successPrompt)
    Invoke-Element (Find-VisibleElementByName $window '发送') 'Send deterministic AI single-action plan'
    $aiDeadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 250
        $storedContexts = Get-Content -LiteralPath $contextFile -Raw | ConvertFrom-Json
        $contextProperty = $storedContexts.PSObject.Properties | Select-Object -First 1
        $storedTurns = @($contextProperty.Value)
    } while ($storedTurns.Count -lt 4 -and [DateTime]::UtcNow -lt $aiDeadline)
    $suggestionText = $storedTurns[-1].content
    $detailedSuggestion = Find-ElementByName $aiConversation ("AI 助手: " + $suggestionText)
    if ($null -eq $detailedSuggestion -or !$suggestionText.Contains('节点与参数：') -or !$suggestionText.Contains('kernelSize=')) {
        throw 'AI suggestion did not render node, interface, parameter, and connection details.'
    }
    Invoke-Element (Find-VisibleElementByName $window '▦') 'Return to Operators activity'
    Start-Sleep -Milliseconds 250
    if ($null -eq (Find-VisibleElementByName $window '算子')) { throw 'Operators sidebar did not reappear.' }
    $appProcess.Refresh()
    if ($appProcess.HasExited) { throw 'App exited while switching activity bar views.' }

    # Click the same Threshold node twice after a successful preview. This is the
    # regression path for replacing a bitmap while the renderer owns the old frame.
    $thresholdNodeButton = Find-GraphNode $window '二值化'
    if ($null -eq $thresholdNodeButton) { throw 'Threshold graph node automation element was not found.' }
    Invoke-Element $thresholdNodeButton 'Threshold node first click'
    Start-Sleep -Milliseconds 1400
    Invoke-Element $thresholdNodeButton 'Threshold node second click'
    Start-Sleep -Milliseconds 1400
    if ($null -eq (Find-ElementByName $window '接口名：Image.Threshold')) { throw 'The property panel did not show the stable operator interface name.' }
    $appProcess.Refresh()
    if ($appProcess.HasExited) { throw "App exited after clicking the same node twice with code $($appProcess.ExitCode)." }
    $cacheCount = (Get-ChildItem -LiteralPath (Join-Path $projectRoot 'images\cache') -Recurse -Filter '*.png').Count
    if ($cacheCount -lt 3) {
        $visibleNames = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { ![string]::IsNullOrWhiteSpace($_) }
        throw "Node preview created $cacheCount caches instead of at least 3. Visible UI: $($visibleNames -join ' | ')"
    }

    # Change the Chinese Threshold property and force focus away so LostFocus applies it.
    $thresholdNameCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        '阈值')
    $editTypeCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $thresholdEditor = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new($thresholdNameCondition, $editTypeCondition))
    if ($null -eq $thresholdEditor) { throw 'Chinese Threshold property editor was not exposed.' }
    $valuePattern = [System.Windows.Automation.ValuePattern]$thresholdEditor.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $thresholdEditor.SetFocus()
    $valuePattern.SetValue('140')
    Invoke-MenuCommand $window '文件' '保存项目' 'Save after property edit'
    Start-Sleep -Milliseconds 900

    # The keyboard shortcut must restore the workflow snapshot from before the edit.
    [VisionStudioNativeMouse]::SetForegroundWindow($appProcess.MainWindowHandle) | Out-Null
    Press-CtrlZ
    Start-Sleep -Milliseconds 500
    Invoke-MenuCommand $window '文件' '保存项目' 'Save after Ctrl+Z'
    Start-Sleep -Milliseconds 700
    $workflowPath = Join-Path $projectRoot 'workflows\main.vflow'
    $workflowAfterUndo = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json
    $thresholdAfterUndo = $workflowAfterUndo.nodes | Where-Object { $_.type -eq 'Image.Threshold' }
    if ([double]$thresholdAfterUndo.parameters.threshold -ne 128) { throw 'Ctrl+Z did not restore the previous Threshold value.' }

    # Exercise the real mouse/context-menu route for downstream branch actions.
    # Each command is invoked from a right-click on the rendered connection, not
    # by calling GraphCanvas methods directly.
    $nodesBeforeBranchActions = @($workflowAfterUndo.nodes).Count
    $connectionsBeforeBranchActions = @($workflowAfterUndo.connections).Count
    Click-AutomationElement (Open-FirstConnectionContextMenu $window $canvas $workflowPath) 'Select downstream nodes'
    Start-Sleep -Milliseconds 250

    Open-FirstConnectionContextMenu $window $canvas $workflowPath | Out-Null
    Click-AutomationElement (Find-VisibleElementByName ([System.Windows.Automation.AutomationElement]::RootElement) '整理后续节点') 'Layout downstream nodes'
    Start-Sleep -Milliseconds 500
    Invoke-MenuCommand $window '文件' '保存项目' 'Save downstream layout'
    Start-Sleep -Milliseconds 700
    $layoutWorkflow = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json
    $firstLayoutEdge = $layoutWorkflow.connections | Select-Object -First 1
    $layoutFrom = $layoutWorkflow.nodes | Where-Object { $_.id -eq $firstLayoutEdge.fromNode }
    $layoutTo = $layoutWorkflow.nodes | Where-Object { $_.id -eq $firstLayoutEdge.toNode }
    if ([double]$layoutTo.x -le [double]$layoutFrom.x) { throw 'Downstream layout did not place the target after its source.' }

    Open-FirstConnectionContextMenu $window $canvas $workflowPath | Out-Null
    Click-AutomationElement (Find-VisibleElementByName ([System.Windows.Automation.AutomationElement]::RootElement) '删除后续节点') 'Delete downstream nodes'
    Start-Sleep -Milliseconds 500
    Invoke-MenuCommand $window '文件' '保存项目' 'Save downstream deletion'
    Start-Sleep -Milliseconds 700
    $deletedBranchWorkflow = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json
    if (@($deletedBranchWorkflow.nodes).Count -ge $nodesBeforeBranchActions) { throw 'Downstream delete did not remove any node.' }
    if (@($deletedBranchWorkflow.connections).Count -ge $connectionsBeforeBranchActions) { throw 'Downstream delete did not remove branch connections.' }
    [VisionStudioNativeMouse]::SetForegroundWindow($appProcess.MainWindowHandle) | Out-Null
    Press-CtrlZ
    Start-Sleep -Milliseconds 500
    Invoke-MenuCommand $window '文件' '保存项目' 'Restore downstream deletion'
    Start-Sleep -Milliseconds 700
    $restoredBranchWorkflow = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json
    if (@($restoredBranchWorkflow.nodes).Count -ne $nodesBeforeBranchActions -or @($restoredBranchWorkflow.connections).Count -ne $connectionsBeforeBranchActions) {
        throw 'Ctrl+Z did not restore the downstream branch deletion.'
    }

    # Select the first connection through its accessibility peer, delete it with the
    # same Delete path used by mouse selection, then restore it with Ctrl+Z.
    $allElements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $connectionElement = $allElements | Where-Object { $_.Current.AutomationId.StartsWith('connection-', [System.StringComparison]::Ordinal) } | Select-Object -First 1
    if ($null -eq $connectionElement) { throw 'Connection automation element was not exposed.' }
    $connectionPoint = Get-FirstConnectionScreenPoint $canvas $workflowPath
    # Move away from the output port so the node/port hit regions cannot win over
    # the connection's deliberately wider hit-test path.
    Click-ScreenPoint ($connectionPoint.X + 28) $connectionPoint.Y
    Start-Sleep -Milliseconds 200
    Press-Key 0x2E
    Start-Sleep -Milliseconds 300
    Invoke-MenuCommand $window '文件' '保存项目' 'Save after connection delete'
    Start-Sleep -Milliseconds 900

    $workflow = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json
    if ($workflow.connections.Count -ne 2) { throw 'Selected connection was not deleted.' }
    if ($workflow.nodes.Count -ne $nodesBeforeBranchActions) { throw 'Deleting a selected connection also deleted a node.' }
    [VisionStudioNativeMouse]::SetForegroundWindow($appProcess.MainWindowHandle) | Out-Null
    Press-CtrlZ
    Start-Sleep -Milliseconds 400
    Invoke-MenuCommand $window '文件' '保存项目' 'Save after connection Ctrl+Z'
    Start-Sleep -Milliseconds 700
    $workflow = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json
    if ($workflow.connections.Count -ne 3) { throw 'Ctrl+Z did not restore the deleted connection.' }
    $thresholdNode = $workflow.nodes | Where-Object { $_.type -eq 'Image.Threshold' }
    if ([double]$thresholdNode.parameters.threshold -ne 128) { throw 'Ctrl+Z result was not persisted.' }

    $autosavePath = Join-Path $projectRoot '.project\autosave\main.vflow'
    $mainWorkflowBeforeExport = (Get-Item -LiteralPath $workflowPath).LastWriteTimeUtc
    $autosaveBeforeExport = (Get-Item -LiteralPath $autosavePath).LastWriteTimeUtc
    Invoke-MenuCommand $window '文件' '导出 C# 到 output' 'Automatic C# export'
    Start-Sleep -Milliseconds 1400
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot 'output\GeneratedVisionAlgorithm.cs'))) {
        throw 'UI export did not write output/GeneratedVisionAlgorithm.cs.'
    }
    if ((Get-Item -LiteralPath $autosavePath).LastWriteTimeUtc -le $autosaveBeforeExport) {
        throw 'Updating the operation record did not trigger workflow autosave.'
    }
    if ((Get-Item -LiteralPath $workflowPath).LastWriteTimeUtc -le $mainWorkflowBeforeExport) {
        throw 'Automatic save did not persist the main workflow.'
    }

    $operationLog = Join-Path $projectRoot '.project\operations.jsonl'
    if (!(Test-Path -LiteralPath $operationLog)) { throw 'Operation log was not persisted.' }
    $operationActions = Get-Content -LiteralPath $operationLog | ForEach-Object { ($_ | ConvertFrom-Json).action }
    if (!$operationActions.Contains('修改参数：阈值') -or !$operationActions.Contains('撤销') -or !$operationActions.Contains('删除连接')) {
        throw "Operation log is missing edit, undo, or connection records. Actions: $($operationActions -join ' | ')"
    }

    Invoke-MenuCommand $window '文件' '关闭文件夹' 'Close folder'
    $newProjectButton = $null
    $welcomeDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 200
        $appProcess.Refresh()
        $newProjectButton = Find-ElementByName $window '＋  新建项目'
    } while ($null -eq $newProjectButton -and !$appProcess.HasExited -and [DateTime]::UtcNow -lt $welcomeDeadline)
    if ($appProcess.HasExited) {
        $errorLog = if (Test-Path -LiteralPath $appStdErr) { Get-Content -LiteralPath $appStdErr -Raw } else { '' }
        throw "App exited after returning to the project page with code $($appProcess.ExitCode). Error log: $errorLog"
    }
    if ($null -eq $newProjectButton) { throw 'Welcome page did not reappear.' }
    $openProjectButton = Find-ElementByName $window '⌂  打开项目'
    if ($null -eq $openProjectButton) { throw 'Open Project button did not appear.' }
    $newBounds = $newProjectButton.Current.BoundingRectangle
    $openBounds = $openProjectButton.Current.BoundingRectangle
    if ([Math]::Abs($newBounds.Width - $openBounds.Width) -gt 1 -or [Math]::Abs($newBounds.Height - $openBounds.Height) -gt 1) {
        throw 'New Project and Open Project buttons do not have identical dimensions.'
    }

    Invoke-NestedMenuCommand $window '视图' '语言' 'English' 'Switch welcome page to English'
    if ($null -eq (Find-ElementByName $window '+  New Project')) { throw 'English project-page resources were not applied.' }
    $visibleChinese = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { !$_.Current.IsOffscreen -and $_.Current.Name -ne '中文' -and $_.Current.Name -match '[\u3400-\u9fff]' } |
        ForEach-Object { $_.Current.Name } | Select-Object -Unique
    if ($visibleChinese.Count -gt 0) { throw "English UI still contains Chinese text: $($visibleChinese -join ' | ')" }
    Invoke-NestedMenuCommand $window 'View' 'Language' 'Chinese' 'Restore welcome page to Chinese'
    if ($null -eq (Find-ElementByName $window '＋  新建项目')) { throw 'Chinese project-page resources were not restored.' }

    Stop-Process -Id $appProcess.Id
    $appProcess.WaitForExit()
    $appProcess = $null

    # Reopen the real editor directly in English, select a node, and reject any
    # visible Chinese UI text. The project name/path are user data and are exempt.
    $env:VISIONSTUDIO_UI_LANGUAGE = 'en-US'
    $englishStdOut = Join-Path $testRoot 'app-english-stdout.log'
    $englishStdErr = Join-Path $testRoot 'app-english-stderr.log'
    $appProcess = Start-Process -FilePath $appExe -ArgumentList @($quotedProjectPath) -PassThru -WindowStyle Hidden -RedirectStandardOutput $englishStdOut -RedirectStandardError $englishStdErr
    $englishDeadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 250
        $appProcess.Refresh()
    } while ($appProcess.MainWindowHandle -eq 0 -and !$appProcess.HasExited -and [DateTime]::UtcNow -lt $englishDeadline)
    if ($appProcess.HasExited -or $appProcess.MainWindowHandle -eq 0) { throw 'English editor did not start.' }
    $script:windowHandle = $appProcess.MainWindowHandle
    $englishWindow = [System.Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)
    $englishCanvas = $null
    do {
        Start-Sleep -Milliseconds 250
        $englishCanvas = Find-ElementByName $englishWindow 'Algorithm Canvas'
        $appProcess.Refresh()
    } while ($null -eq $englishCanvas -and !$appProcess.HasExited -and [DateTime]::UtcNow -lt $englishDeadline)
    if ($null -eq $englishCanvas) { throw 'English algorithm canvas did not appear.' }
    $englishThreshold = Find-GraphNode $englishWindow 'Global Threshold'
    Invoke-Element $englishThreshold 'English Threshold node'
    Start-Sleep -Milliseconds 1200
    $englishChinese = $englishWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object {
            !$_.Current.IsOffscreen -and $_.Current.AutomationId -ne 'ProjectText' -and
            $_.Current.Name -ne '中文' -and $_.Current.Name -match '[\u3400-\u9fff]'
        } | ForEach-Object { $_.Current.Name } | Select-Object -Unique
    if ($englishChinese.Count -gt 0) { throw "English editor still contains Chinese text: $($englishChinese -join ' | ')" }
    Stop-Process -Id $appProcess.Id
    $appProcess.WaitForExit()
    $appProcess = $null

    Invoke-DotNet @($cliDll, 'validate', $workflowPath)
    Invoke-DotNet @($cliDll, 'run', $workflowPath, (Join-Path $projectRoot 'images\output\cli-result.png'))
    Invoke-DotNet @($cliDll, 'export', $workflowPath, (Join-Path $projectRoot 'generated\Algorithm.cs'))
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot 'images\output\cli-result.png'))) { throw 'CLI run did not create output.' }
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot 'generated\Algorithm.cs'))) { throw 'C# export did not create output.' }

    Write-Output 'UI workflow suite passed: menus/about, folders, double click, cache, property edit, Ctrl+Z, real connection context-menu branch select/layout/delete, line delete/undo, operation log, export, close folder and CLI.'
}
finally {
    if ($null -eq $previousDisableOpenFolder) { Remove-Item Env:VISIONSTUDIO_DISABLE_OPEN_FOLDER -ErrorAction SilentlyContinue }
    else { $env:VISIONSTUDIO_DISABLE_OPEN_FOLDER = $previousDisableOpenFolder }
    if ($null -eq $previousUiLanguage) { Remove-Item Env:VISIONSTUDIO_UI_LANGUAGE -ErrorAction SilentlyContinue }
    else { $env:VISIONSTUDIO_UI_LANGUAGE = $previousUiLanguage }
    if ($null -eq $previousUiTheme) { Remove-Item Env:VISIONSTUDIO_UI_THEME -ErrorAction SilentlyContinue }
    else { $env:VISIONSTUDIO_UI_THEME = $previousUiTheme }
    if ($null -eq $previousTestMode) { Remove-Item Env:VISIONSTUDIO_TEST_MODE -ErrorAction SilentlyContinue }
    else { $env:VISIONSTUDIO_TEST_MODE = $previousTestMode }
    if ($null -eq $previousTestConfig) { Remove-Item Env:VISIONSTUDIO_TEST_CONFIG_DIRECTORY -ErrorAction SilentlyContinue }
    else { $env:VISIONSTUDIO_TEST_CONFIG_DIRECTORY = $previousTestConfig }
    if ($null -eq $previousTestEndpoint) { Remove-Item Env:VISIONSTUDIO_AI_TEST_ENDPOINT -ErrorAction SilentlyContinue }
    else { $env:VISIONSTUDIO_AI_TEST_ENDPOINT = $previousTestEndpoint }
    if ($null -ne $fakeAiJob) {
        Stop-Job $fakeAiJob -ErrorAction SilentlyContinue
        Remove-Job $fakeAiJob -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $appProcess) {
        $appProcess.Refresh()
        if (!$appProcess.HasExited) { Stop-Process -Id $appProcess.Id }
        $appProcess.WaitForExit()
    }
    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
