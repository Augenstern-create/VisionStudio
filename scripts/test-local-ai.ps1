[CmdletBinding()]
param(
    [string]$Model = 'E:\models\Qwen2.5-VL-3B-Instruct-Q4_K_M.gguf',
    [string]$Mmproj = 'E:\models\Qwen2.5-VL-3B-Instruct.mmproj-fp16.gguf',
    [string]$LlamaServer = 'D:\Programs\llama\llama-server.exe',
    [string]$ProjectPath = ''
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:VISIONSTUDIO_RUN_LOCAL_AI = '1'
$env:VISIONSTUDIO_AI_MODEL = [System.IO.Path]::GetFullPath($Model)
$env:VISIONSTUDIO_AI_MMPROJ = [System.IO.Path]::GetFullPath($Mmproj)
$env:VISIONSTUDIO_LLAMA_SERVER = [System.IO.Path]::GetFullPath($LlamaServer)
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    Remove-Item Env:VISIONSTUDIO_AI_TEST_PROJECT -ErrorAction SilentlyContinue
}
else {
    $env:VISIONSTUDIO_AI_TEST_PROJECT = [System.IO.Path]::GetFullPath($ProjectPath)
}
& dotnet test (Join-Path $workspaceRoot 'tests\VisionStudio.Tests\VisionStudio.Tests.csproj') -c Release `
    --filter 'FullyQualifiedName~LocalLlamaIntegrationTests' -p:UsedAvaloniaProducts=
if ($LASTEXITCODE -ne 0) { throw 'Local llama.cpp integration test failed.' }
