[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [switch]$AllowProviderPlugin
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($Path)
if (!(Test-Path -LiteralPath $root)) { throw "Package path does not exist: $root" }

$forbidden = Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object {
    $_.Extension -in @('.gguf', '.ggml', '.safetensors') -or
    (!$AllowProviderPlugin -and $_.Name -eq 'VisionStudio.Ai.LlamaCpp.dll') -or
    $_.Name -match '^(llama|ggml|cublas|cudart).*(\.exe|\.dll|\.so)$'
}
if ($forbidden) {
    throw "Main package contains optional AI payload:`n$($forbidden.FullName -join [Environment]::NewLine)"
}

Write-Output "AI package boundary passed: no forbidden model, llama.cpp, or CUDA payload under $root"
