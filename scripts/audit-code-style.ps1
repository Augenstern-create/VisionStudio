[CmdletBinding()]
param(
  [string]$Path = '.',
  [string]$Baseline = '',
  [switch]$Compiler,
  [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scanRoot = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot $Path))
$workspacePrefix = $workspaceRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
  [System.IO.Path]::DirectorySeparatorChar

if ($scanRoot -ne $workspaceRoot -and
    !$scanRoot.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
  throw "The audit path must stay inside the workspace: $scanRoot"
}

$extensions = @('.cs', '.axaml', '.xml', '.html', '.css', '.js', '.json', '.ps1', '.psm1', '.sh')
$excludedSegments = @(
  "${([System.IO.Path]::DirectorySeparatorChar)}bin${([System.IO.Path]::DirectorySeparatorChar)}",
  "${([System.IO.Path]::DirectorySeparatorChar)}obj${([System.IO.Path]::DirectorySeparatorChar)}",
  "${([System.IO.Path]::DirectorySeparatorChar)}artifacts${([System.IO.Path]::DirectorySeparatorChar)}",
  "${([System.IO.Path]::DirectorySeparatorChar)}node_modules${([System.IO.Path]::DirectorySeparatorChar)}"
)

$files = Get-ChildItem -LiteralPath $scanRoot -File -Recurse | Where-Object {
  $fullName = $_.FullName
  $_.Extension -in $extensions -and
  !($excludedSegments | Where-Object {
    $fullName.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0
  })
}

$issues = [System.Collections.Generic.List[object]]::new()
foreach ($file in $files) {
  $relativePath = $file.FullName.Substring($workspacePrefix.Length).Replace('\', '/')
  $lines = [System.IO.File]::ReadAllLines($file.FullName)
  for ($index = 0; $index -lt $lines.Length; $index++) {
    $line = $lines[$index]
    if ($line.Contains("`t")) {
      $issues.Add([pscustomobject]@{ Rule = 'TAB'; File = $relativePath; Line = $index + 1; Detail = 'Tab character' })
    }
    if ($line -match '[ \t]+$') {
      $issues.Add([pscustomobject]@{ Rule = 'TRAILING'; File = $relativePath; Line = $index + 1; Detail = 'Trailing whitespace' })
    }
    if ($file.Extension -in @('.cs', '.js') -and $line.Length -gt 100) {
      $issues.Add([pscustomobject]@{ Rule = 'LINE100'; File = $relativePath; Line = $index + 1; Detail = "$($line.Length) columns" })
    }
    if ($file.Extension -eq '.cs' -and $line -match '^\s*\{\s*$') {
      $issues.Add([pscustomobject]@{ Rule = 'CS-BRACE'; File = $relativePath; Line = $index + 1; Detail = 'Opening brace must remain on the declaration line' })
    }
  }
}

$summary = $issues | Group-Object Rule | Sort-Object Name | ForEach-Object {
  [pscustomobject]@{ Rule = $_.Name; Count = $_.Count }
}
$compilerCounts = @{}

Write-Output "Google-style audit root: $scanRoot"
Write-Output "Files checked: $($files.Count)"
if ($summary) {
  $summary | Format-Table -AutoSize | Out-String | Write-Output
  Write-Output 'First 40 findings:'
  $issues | Select-Object -First 40 | Format-Table -AutoSize | Out-String | Write-Output
} else {
  Write-Output 'No lexical style findings.'
}

if ($Compiler) {
  if ($scanRoot -ne $workspaceRoot) {
    throw 'Compiler style analysis currently requires the workspace root.'
  }
  $savedErrorPreference = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $formatOutput = & dotnet format (Join-Path $workspaceRoot 'VisionStudio.sln') style `
    --verify-no-changes --no-restore --severity info --verbosity minimal 2>&1
  $formatExit = $LASTEXITCODE
  $ErrorActionPreference = $savedErrorPreference
  $diagnostics = @($formatOutput | Select-String ': (error|warning|info) [A-Z]+\d+:')
  foreach ($group in ($diagnostics | ForEach-Object {
    if ($_.Line -match ': (?:error|warning|info) ([A-Z]+\d+):') { $matches[1] }
  } | Group-Object)) {
    $compilerCounts[$group.Name] = $group.Count
  }
  Write-Output "Compiler style analysis: exit=$formatExit; findings=$($diagnostics.Count)"
  $compilerCounts.GetEnumerator() | Sort-Object Name | Format-Table Name, Value -AutoSize |
    Out-String | Write-Output
  if ($formatExit -notin @(0, 2) -or ($formatExit -ne 0 -and $diagnostics.Count -eq 0)) {
    $formatOutput | Select-Object -Last 20 | Write-Output
    throw "dotnet format failed unexpectedly with exit code $formatExit."
  }
}

if (![string]::IsNullOrWhiteSpace($Baseline)) {
  $baselinePath = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot $Baseline))
  if (!$baselinePath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The baseline path must stay inside the workspace: $baselinePath"
  }
  $limits = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
  foreach ($property in $limits.PSObject.Properties) {
    $rule = $property.Name
    $actual = if ($compilerCounts.ContainsKey($rule)) {
      $compilerCounts[$rule]
    } else {
      @($issues | Where-Object Rule -eq $rule).Count
    }
    if ($actual -gt [int]$property.Value) {
      throw "Style debt increased for ${rule}: actual=$actual; baseline=$($property.Value)."
    }
  }
  Write-Output "Baseline check passed: $baselinePath"
}

if ($Strict -and ($issues.Count -gt 0 -or ($compilerCounts.Values | Measure-Object -Sum).Sum -gt 0)) {
  throw 'Google-style audit failed because style findings remain.'
}
