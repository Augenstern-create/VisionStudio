param(
    [string]$ProjectPath
)

$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $workspaceRoot 'samples\DemoProject\DemoProject.vstudio'
}
$ProjectPath = [System.IO.Path]::GetFullPath($ProjectPath)
$appProject = Join-Path $workspaceRoot 'src\VisionStudio.App\VisionStudio.App.csproj'
$appExe = Join-Path $workspaceRoot 'src\VisionStudio.App\bin\Release\net9.0\VisionStudio.exe'

dotnet build $appProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release app build failed.' }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VisionStudioBackMenuMouse
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
'@

function Invoke-Element($Element) {
    if ($null -eq $Element) { throw 'Expected menu element was not found.' }
    $Element.SetFocus()
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
}

$quotedProjectPath = '"' + $ProjectPath + '"'
$appProcess = Start-Process -FilePath $appExe -ArgumentList @($quotedProjectPath) -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 250
        $appProcess.Refresh()
    } while ($appProcess.MainWindowHandle -eq 0 -and !$appProcess.HasExited -and [DateTime]::UtcNow -lt $deadline)

    if ($appProcess.HasExited) { throw "App exited before interaction with code $($appProcess.ExitCode)." }
    if ($appProcess.MainWindowHandle -eq 0) { throw 'Main window was not created.' }

    $window = [System.Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)
    [VisionStudioBackMenuMouse]::SetForegroundWindow($appProcess.MainWindowHandle) | Out-Null
    $fileCondition = [System.Windows.Automation.OrCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, '文件'),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'File'))
    $fileMenu = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $fileCondition)
    Invoke-Element $fileMenu
    Start-Sleep -Milliseconds 250
    $closeCondition = [System.Windows.Automation.OrCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, '关闭文件夹'),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Close Folder'))
    $closeFolder = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $closeCondition)
    Invoke-Element $closeFolder
    Start-Sleep -Milliseconds 800

    $appProcess.Refresh()
    if ($appProcess.HasExited) { throw "App exited after closing the folder with code $($appProcess.ExitCode)." }

    $newProjectCondition = [System.Windows.Automation.OrCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, '＋  新建项目'),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, '+  New Project'))
    $newProjectButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $newProjectCondition)
    if ($null -eq $newProjectButton) { throw 'Welcome page did not reappear after closing the folder.' }

    Write-Output 'Close folder passed: process alive, project cleared, and welcome page visible.'
}
finally {
    if (!$appProcess.HasExited) { Stop-Process -Id $appProcess.Id }
}
