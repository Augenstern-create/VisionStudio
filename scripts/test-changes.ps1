[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Dimension,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $workspaceRoot 'VisionStudio.sln'
$testProject = Join-Path $workspaceRoot 'tests\VisionStudio.Tests\VisionStudio.Tests.csproj'

$allDimensions = @('CodeStyle', 'UiStyle', 'Typography', 'CanvasInteraction', 'Workflow', 'Operators', 'Project', 'CodeExport', 'RuntimeApp', 'Ai', 'DesktopInteraction', 'Web', 'Packaging', 'EndToEnd')
$requested = @($Dimension | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$invalid = @($requested | Where-Object { $_ -ne 'All' -and $_ -notin $allDimensions })
if ($invalid.Count -gt 0) {
    throw "Unknown test dimension: $($invalid -join ', '). Valid dimensions: $($allDimensions -join ', '), All."
}
$selected = if ($requested -contains 'All') { $allDimensions } else { @($requested | Select-Object -Unique) }

function Invoke-DotNetTest([string]$Filter) {
    & dotnet test $testProject -c Release --no-build --filter $Filter
    if ($LASTEXITCODE -ne 0) { throw "Tests failed for filter: $Filter" }
}

if (!$NoBuild) {
    & dotnet build $solution -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
}

foreach ($item in $selected) {
    Write-Output "--- Testing dimension: $item ---"
    switch ($item) {
        'CodeStyle' {
            & (Join-Path $PSScriptRoot 'audit-code-style.ps1') -Compiler -Baseline '.code-style-baseline.json'
        }
        'UiStyle' {
            & (Join-Path $PSScriptRoot 'test-ui-style.ps1')
        }
        'Typography' {
            & (Join-Path $PSScriptRoot 'test-typography.ps1')
            Invoke-DotNetTest 'FullyQualifiedName~LocalizationTests'
        }
        'CanvasInteraction' {
            Invoke-DotNetTest 'FullyQualifiedName~GraphCanvasTests'
        }
        'Workflow' {
            Invoke-DotNetTest 'FullyQualifiedName~WorkflowTests|FullyQualifiedName~WorkflowAutomationTests'
        }
        'Operators' {
            Invoke-DotNetTest 'FullyQualifiedName~OpenCvIntegrationTests|FullyQualifiedName~ExtendedOperatorTests'
        }
        'Project' {
            Invoke-DotNetTest 'FullyQualifiedName~ProjectTests|FullyQualifiedName~FileAssociationTests'
        }
        'CodeExport' {
            Invoke-DotNetTest 'FullyQualifiedName~WorkflowTests|FullyQualifiedName~ExtendedOperatorTests'
            & (Join-Path $PSScriptRoot 'test-csharp-export.ps1') -TargetNodeId 'metrics' -NoBuild
            & (Join-Path $PSScriptRoot 'test-csharp-export.ps1') -TargetNodeId 'metrics_2' -NoBuild -ExpectUnsupported
        }
        'RuntimeApp' {
            Invoke-DotNetTest 'FullyQualifiedName~RuntimeAppTests'
        }
        'Ai' {
            Invoke-DotNetTest 'FullyQualifiedName~AiAssistantTests|FullyQualifiedName~AiProviderLifetimeTests|FullyQualifiedName~CrackMeasurementTests|FullyQualifiedName~ChatMessageControlTests|FullyQualifiedName~LocalLlamaIntegrationTests|FullyQualifiedName~VisionAiOperatorTests'
            & (Join-Path $PSScriptRoot 'test-ai-ui.ps1')
            & (Join-Path $PSScriptRoot 'test-ai-packaging.ps1') -Path (Join-Path $workspaceRoot 'src\VisionStudio.App\bin\Release\net9.0')
        }
        'DesktopInteraction' {
            & (Join-Path $PSScriptRoot 'test-back-navigation.ps1')
        }
        'Web' {
            Invoke-DotNetTest 'FullyQualifiedName~WebProjectStoreTests'
            & (Join-Path $PSScriptRoot 'test-web.ps1') -NoBuild
        }
        'Packaging' {
            & (Join-Path $PSScriptRoot 'test-release-metadata.ps1')
            & (Join-Path $PSScriptRoot 'build-installers.ps1') -Platform All -NoPublish
            & (Join-Path $PSScriptRoot 'test-windows-installer.ps1')
            & (Join-Path $PSScriptRoot 'test-ai-packaging.ps1') -Path (Join-Path $workspaceRoot 'artifacts\releases')
            $linuxTest = '/mnt/' + $workspaceRoot.Substring(0, 1).ToLowerInvariant() + $workspaceRoot.Substring(2).Replace('\', '/') + '/scripts/test-linux-installer.sh'
            & wsl.exe bash $linuxTest
        }
        'EndToEnd' {
            & (Join-Path $PSScriptRoot 'test-ui-workflows.ps1')
        }
    }
    if (!$?) { throw "Dimension failed: $item" }
}

Write-Output ('Selected test dimensions passed: ' + ($selected -join ', '))
