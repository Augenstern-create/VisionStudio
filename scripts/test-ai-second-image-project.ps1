$ErrorActionPreference = 'Stop'

$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectRoot = Join-Path $workspaceRoot 'samples\DemoProject'
$projectFile = Get-ChildItem -LiteralPath $projectRoot -Filter '*.vstudio' | Select-Object -First 1
$workflowPath = Join-Path $projectRoot 'workflows\main.vflow'
$appExe = Join-Path $workspaceRoot 'tests\VisionStudio.Tests\bin\Debug\net9.0\VisionStudio.exe'
$contextPath = Join-Path (Split-Path $appExe) 'config\ai-contexts.json'
$img01 = '4102953d3b584d9aaf0c224c62d3e5b0'
$img02 = '278f941a30d74260984680aacf0fce7a'
$prompt = '[IMG-02] 加载图像 我需要一个能测量裂纹数量与像素长度的流程'
$process = $null

function Find-ElementByName($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ElementContainingName($Root, [string]$Text) {
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { !$_.Current.IsOffscreen -and $_.Current.Name.Contains($Text, [System.StringComparison]::Ordinal) } |
        Select-Object -First 1
}

function Invoke-Element($Element, [string]$Label) {
    if ($null -eq $Element) { throw "UI element was not found: $Label" }
    $pattern = [System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Write-Output "Invoked: $Label"
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds, [string]$Failure) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

try {
    if ($null -eq $projectFile) { throw 'DemoProject has no .vstudio project file.' }
    $before = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json
    if (@($before.nodes | Where-Object id -like '*_2').Count -ne 0) {
        throw 'DemoProject must be cleaned before this test; duplicate _2 nodes remain.'
    }
    if ($null -ne ($before.connections | Where-Object { $_.fromNode -eq $img01 -and $_.toNode -eq 'gray_2' })) {
        throw 'The old incorrect IMG-01 connection still exists before testing.'
    }

    $storedContexts = Get-Content -LiteralPath $contextPath -Raw | ConvertFrom-Json
    $allStoredMessages = $storedContexts.PSObject.Properties.Value | ForEach-Object { $_ }
    if ($null -eq ($allStoredMessages | Where-Object { $_.content -like '*[[]IMG-01[]]*' } | Select-Object -First 1)) {
        throw 'The existing conversation history was not found before launching the UI.'
    }

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $process = Start-Process -FilePath $appExe -ArgumentList ('"' + $projectFile.FullName + '"') -PassThru
    Wait-Until {
        $process.Refresh()
        if ($process.HasExited) { throw "Vision Studio exited with code $($process.ExitCode)." }
        $process.MainWindowHandle -ne [IntPtr]::Zero
    } 20 'Vision Studio did not create its main window.' | Out-Null
    $process.Refresh()
    $windowHandle = $process.MainWindowHandle
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    Wait-Until { Find-ElementByName $window '算法画布' } 20 'DemoProject did not open in the editor.' | Out-Null

    Invoke-Element (Find-ElementByName $window '✦') 'AI activity'
    $conversation = Wait-Until { Find-ElementByName $window 'AI 对话' } 15 'AI conversation panel did not open.'
    if ($null -eq (Find-ElementContainingName $conversation '[IMG-01]')) {
        throw 'Existing IMG-01 conversation was not rendered after opening the AI panel.'
    }
    if ($null -eq (Find-ElementContainingName $conversation '[IMG-02]')) {
        throw 'Existing IMG-02 conversation was not rendered after opening the AI panel.'
    }
    Write-Output 'Existing IMG-01 and IMG-02 conversation turns are visible.'

    $promptBox = Find-ElementByName $window '描述需求，可写 IMG-01、图片编号或节点名称，也可把图像节点编号拖到这里…'
    if ($null -eq $promptBox) { throw 'AI prompt box was not found.' }
    $valuePattern = [System.Windows.Automation.ValuePattern]$promptBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue($prompt)
    Invoke-Element (Find-ElementByName $window '发送') 'send IMG-02 request'

    $apply = Wait-Until {
        $candidate = Find-ElementByName $window '应用'
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } 240 'The local model did not produce an applicable plan within 240 seconds.'

    $conversation = Find-ElementByName $window 'AI 对话'
    $sourceDescription = "$img02.Image → gray_2.Image"
    $latestSuggestion = Find-ElementContainingName $conversation $sourceDescription
    if ($null -eq $latestSuggestion) {
        $messages = $conversation.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { ![string]::IsNullOrWhiteSpace($_) }
        throw "The suggestion did not bind gray_2 to IMG-02. Visible conversation: $($messages -join ' | ')"
    }
    if ($latestSuggestion.Current.Name.Contains("$img01.Image → gray_2.Image", [System.StringComparison]::Ordinal)) {
        throw 'The latest suggestion contains both IMG-02 and the incorrect IMG-01 source.'
    }
    Write-Output 'Suggestion source is IMG-02 and not IMG-01.'

    Invoke-Element $apply 'apply IMG-02 plan'
    Wait-Until {
        try {
            $saved = Get-Content -LiteralPath $workflowPath -Raw | ConvertFrom-Json
            $correct = $saved.connections | Where-Object { $_.fromNode -eq $img02 -and $_.toNode -eq 'gray_2' }
            $wrong = $saved.connections | Where-Object { $_.fromNode -eq $img01 -and $_.toNode -eq 'gray_2' }
            $target = $saved.nodes | Where-Object id -eq 'metrics_2'
            $correct -and !$wrong -and $target
        } catch { $false }
    } 60 'Applied workflow was not saved with the expected IMG-02 branch.' | Out-Null

    $afterContexts = Get-Content -LiteralPath $contextPath -Raw | ConvertFrom-Json
    $afterMessages = $afterContexts.PSObject.Properties.Value | ForEach-Object { $_ }
    if ($null -eq ($afterMessages | Where-Object { $_.content -eq $prompt } | Select-Object -First 1)) {
        throw 'The new IMG-02 user turn was not persisted in conversation history.'
    }
    if ($null -eq ($afterMessages | Where-Object { $_.content -like '*[[]IMG-01[]]*' } | Select-Object -First 1)) {
        throw 'Existing conversation turns were lost after the new request.'
    }
    Write-Output 'AI second-image project test passed: history preserved, request resent, plan applied, source verified.'
}
finally {
    if ($null -ne $process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
}
