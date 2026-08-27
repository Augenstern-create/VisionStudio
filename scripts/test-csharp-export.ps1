[CmdletBinding()]
param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\samples\DemoProject'),
    [string]$TargetNodeId = 'metrics',
    [switch]$NoBuild,
    [switch]$ExpectUnsupported
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectRoot = [System.IO.Path]::GetFullPath($ProjectPath)
$workflowPath = Join-Path $projectRoot 'workflows\main.vflow'
$outputPath = Join-Path $projectRoot 'output\GeneratedVisionAlgorithm.cs'
$checkerProject = Join-Path $workspaceRoot 'tests\VisionStudio.ExportExecutionRunner\VisionStudio.ExportExecutionRunner.csproj'
$cliProject = Join-Path $workspaceRoot 'src\VisionStudio.Cli\VisionStudio.Cli.csproj'

if (!(Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
    throw "Workflow does not exist: $workflowPath"
}

$exportArguments = @('run', '--project', $cliProject, '-c', 'Release')
if ($NoBuild) { $exportArguments += '--no-build' }
$exportArguments += @('--', 'export', $workflowPath, $outputPath, $TargetNodeId)
if ($ExpectUnsupported -and (Test-Path -LiteralPath $outputPath)) {
    Remove-Item -LiteralPath $outputPath -Force
}
& dotnet @exportArguments
$exportExitCode = $LASTEXITCODE
if ($ExpectUnsupported) {
    if ($exportExitCode -eq 0) { throw 'CLI unexpectedly exported a RuntimeFrame-dependent branch.' }
    if (Test-Path -LiteralPath $outputPath) { throw 'Rejected C# export left a stale source file.' }
    Write-Output "C# export boundary passed: target=$TargetNodeId depends on Input.RuntimeFrame and was rejected without an output file."
    return
}
if ($exportExitCode -ne 0) { throw 'CLI C# export failed.' }
if (!(Test-Path -LiteralPath $outputPath -PathType Leaf)) { throw "Export did not create: $outputPath" }

$source = Get-Content -LiteralPath $outputPath -Raw
foreach ($forbidden in @('VisionStudio.Core', 'VisionStudio.Operators.OpenCv', 'WorkflowJson.Deserialize', 'WorkflowExecutor')) {
    if ($source.Contains($forbidden, [StringComparison]::Ordinal)) {
        throw "Exported source still depends on the Vision Studio runtime: $forbidden"
    }
}
foreach ($required in @('CvInvoke.GaussianBlur', 'MorphOp.Blackhat', 'ThresholdType.Otsu', 'CvInvoke.FindContours', 'CvInvoke.MinAreaRect')) {
    if (!$source.Contains($required, [StringComparison]::Ordinal)) { throw "Exported source is missing OpenCV operation: $required" }
}

$isolatedRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('VisionStudio-CSharpExport-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($isolatedRoot) | Out-Null
try {
    $isolatedSource = Join-Path $isolatedRoot 'GeneratedVisionAlgorithm.cs'
    $isolatedProject = Join-Path $isolatedRoot 'GeneratedCheck.csproj'
    $isolatedProgram = Join-Path $isolatedRoot 'Program.cs'
    Copy-Item -LiteralPath $outputPath -Destination $isolatedSource
    Copy-Item -LiteralPath $checkerProject -Destination $isolatedProject
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $checkerProject) 'Program.cs') -Destination $isolatedProgram
    & dotnet build $isolatedProject -c Release --nologo "-p:GeneratedSource=$isolatedSource"
    if ($LASTEXITCODE -ne 0) { throw 'Exported C# compilation failed in the independent runner.' }
    & dotnet run --project $isolatedProject -c Release --no-build "-p:GeneratedSource=$isolatedSource" -- $projectRoot
    if ($LASTEXITCODE -ne 0) { throw 'Exported C# execution failed in the independent runner.' }
}
finally {
    if (Test-Path -LiteralPath $isolatedRoot) { Remove-Item -LiteralPath $isolatedRoot -Recurse -Force }
}

Write-Output "C# export passed independently: target=$TargetNodeId; no Vision Studio runtime references; source generated, compiled, and executed."
