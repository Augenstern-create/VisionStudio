$ErrorActionPreference = 'Stop'

$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceDirectory = Join-Path $workspaceRoot 'docs\project-handbook'
$outputDirectory = Join-Path $workspaceRoot 'artifacts\project-handbook'
$chapterDirectory = Join-Path $outputDirectory 'chapters'
$combinedMarkdown = Join-Path $outputDirectory 'VisionStudio-项目讲解手册.md'
$html = Join-Path $outputDirectory 'VisionStudio-项目讲解手册.html'
$epub = Join-Path $outputDirectory 'VisionStudio-项目讲解手册.epub'
$docx = Join-Path $outputDirectory 'VisionStudio-项目讲解手册.docx'
$zip = Join-Path $outputDirectory 'VisionStudio-项目讲解资料包.zip'
$css = Join-Path $sourceDirectory 'mobile.css'
$chapters = Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.md' | Sort-Object Name

if ($chapters.Count -lt 13) { throw "Expected at least 13 handbook chapters; found $($chapters.Count)." }
$pandoc = Get-Command pandoc -ErrorAction Stop

New-Item -ItemType Directory -Path $chapterDirectory -Force | Out-Null
Copy-Item -LiteralPath $chapters.FullName -Destination $chapterDirectory -Force
Copy-Item -LiteralPath $css -Destination $chapterDirectory -Force

$frontMatter = @"
---
title: "Vision Studio 项目讲解手册"
subtitle: "架构、技术栈、实现、边界、测试与答疑"
author: "Vision Studio"
date: "$(Get-Date -Format 'yyyy-MM-dd')"
lang: zh-CN
---

"@
$parts = @($frontMatter)
foreach ($chapter in $chapters) {
    $parts += Get-Content -LiteralPath $chapter.FullName -Raw
    $parts += "`n`n<div style=`"page-break-after: always;`"></div>`n`n"
}
[System.IO.File]::WriteAllText($combinedMarkdown, ($parts -join "`n"), [System.Text.UTF8Encoding]::new($false))

& $pandoc.Source $combinedMarkdown --standalone --self-contained --toc --toc-depth=3 --css $css --metadata lang=zh-CN --output $html
if ($LASTEXITCODE -ne 0) { throw 'Pandoc failed to generate HTML.' }
& $pandoc.Source $combinedMarkdown --standalone --toc --toc-depth=3 --css $css --metadata lang=zh-CN --output $epub
if ($LASTEXITCODE -ne 0) { throw 'Pandoc failed to generate EPUB.' }
& $pandoc.Source $combinedMarkdown --toc --toc-depth=3 --metadata lang=zh-CN --output $docx
if ($LASTEXITCODE -ne 0) { throw 'Pandoc failed to generate DOCX.' }

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $chapterDirectory, $combinedMarkdown, $html, $epub, $docx -DestinationPath $zip -CompressionLevel Optimal

$outputs = @($combinedMarkdown, $html, $epub, $docx, $zip)
foreach ($path in $outputs) {
    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output "$($item.Name)`t$($item.Length) bytes`tSHA256 $hash"
}
