param(
    [string]$PackagePath,
    [string]$DetectionModel,
    [string]$TestImage
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$siblingRoot = Join-Path (Split-Path -Parent $repositoryRoot) 'VisionAI'
if ([string]::IsNullOrWhiteSpace($PackagePath)) { $PackagePath = Join-Path $siblingRoot 'out\packages\VisionAI-0.1.0-windows-x64.zip' }
if ([string]::IsNullOrWhiteSpace($DetectionModel)) { $DetectionModel = Join-Path $siblingRoot 'models\yolo11m-detect-fp32.onnx' }
if ([string]::IsNullOrWhiteSpace($TestImage)) {
    $positiveImage = 'E:\data\3.png'
    $TestImage = if (Test-Path -LiteralPath $positiveImage) { $positiveImage } else { Join-Path $repositoryRoot 'tests\image\1.png' }
}
$runtime = Join-Path $repositoryRoot 'build\visionai-test-runtime'
& (Join-Path $PSScriptRoot 'install-visionai-runtime.ps1') -PackagePath $PackagePath -Destination $runtime
$anomalyModel = Join-Path $repositoryRoot 'build\visionai-anomaly-contract.onnx'
$visionAiPython = Join-Path $siblingRoot '.conda\python.exe'
if (Test-Path -LiteralPath $visionAiPython) {
    $generator = Join-Path $siblingRoot 'tests\python'
    & $visionAiPython -c "import sys; from pathlib import Path; sys.path.insert(0, r'$generator'); from c_api_anomaly_test import create_contract_model; create_contract_model(Path(r'$anomalyModel'))"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to create the VisionAI anomaly contract fixture.' }
}

$oldRuntime = $env:VISIONSTUDIO_VISIONAI_RUNTIME
$oldModel = $env:VISIONSTUDIO_VISIONAI_DETECTION_MODEL
$oldImage = $env:VISIONSTUDIO_VISIONAI_TEST_IMAGE
try {
    $env:VISIONSTUDIO_VISIONAI_RUNTIME = Join-Path $runtime 'bin'
    $env:VISIONSTUDIO_VISIONAI_DETECTION_MODEL = [IO.Path]::GetFullPath($DetectionModel)
    $env:VISIONSTUDIO_VISIONAI_SEGMENTATION_MODEL = Join-Path $siblingRoot 'models\yolo11m-seg-fp32.onnx'
    $env:VISIONSTUDIO_VISIONAI_OCR_DETECTION_MODEL = Join-Path $siblingRoot 'models\ocr\ppocrv5\ppocrv5-server-det-fp32.onnx'
    $env:VISIONSTUDIO_VISIONAI_OCR_RECOGNITION_MODEL = Join-Path $siblingRoot 'models\ocr\ppocrv5\ppocrv5-server-rec-fp32.onnx'
    $env:VISIONSTUDIO_VISIONAI_OCR_DICTIONARY = Join-Path $siblingRoot 'models\ocr\ppocrv5\ppocrv5-server-rec-dict.txt'
    if (Test-Path -LiteralPath $anomalyModel) { $env:VISIONSTUDIO_VISIONAI_ANOMALY_MODEL = $anomalyModel }
    $env:VISIONSTUDIO_VISIONAI_TEST_IMAGE = [IO.Path]::GetFullPath($TestImage)
    dotnet test (Join-Path $repositoryRoot 'tests\VisionStudio.Tests\VisionStudio.Tests.csproj') -c Debug --filter 'FullyQualifiedName~VisionAiOperatorTests'
    if ($LASTEXITCODE -ne 0) { throw "VisionAI operator tests failed with exit code $LASTEXITCODE" }
}
finally {
    $env:VISIONSTUDIO_VISIONAI_RUNTIME = $oldRuntime
    $env:VISIONSTUDIO_VISIONAI_DETECTION_MODEL = $oldModel
    $env:VISIONSTUDIO_VISIONAI_TEST_IMAGE = $oldImage
}
