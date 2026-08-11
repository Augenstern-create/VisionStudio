[CmdletBinding()]
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $workspaceRoot 'src\VisionStudio.Web\VisionStudio.Web.csproj'
$assembly = Join-Path $workspaceRoot 'src\VisionStudio.Web\bin\Release\net9.0\VisionStudio.Web.dll'
$sample = Join-Path $workspaceRoot 'samples\DemoProject\images\test\input.png'
$clientScript = Join-Path $workspaceRoot 'src\VisionStudio.Web\wwwroot\app.js'
$testRoot = Join-Path $workspaceRoot ('artifacts\web-automated-test-' + [Guid]::NewGuid().ToString('N'))
$token = 'visionstudio-web-automated-test-token'
$server = $null

function Assert-True([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $parameters = @{ Method = $Method; Uri = "$script:baseUrl$Path"; Headers = @{ 'X-VisionStudio-Token' = $token } }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 20
    }
    Invoke-RestMethod @parameters
}

try {
    if (!$NoBuild) {
        & dotnet build $project -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Web Release build failed.' }
    }
    if (!(Test-Path -LiteralPath $assembly)) { throw "Web assembly is missing: $assembly" }
    if (!(Test-Path -LiteralPath $sample)) { throw "Test image is missing: $sample" }
    $clientSource = Get-Content -LiteralPath $clientScript -Raw
    Assert-True ($clientSource.Contains('const categoryNames=') -and $clientSource.Contains('state.language==="en-US"?port.name')) 'English Web labels are not using localized categories and stable port names.'

    [System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $script:baseUrl = "http://127.0.0.1:$port"

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new('dotnet', "`"$assembly`"")
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = Split-Path -Parent $project
    $startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $startInfo.Environment['ASPNETCORE_URLS'] = $script:baseUrl
    $startInfo.Environment['VISIONSTUDIO_WEB_TOKEN'] = $token
    $startInfo.Environment['VisionStudio__DataRoot'] = $testRoot
    $startInfo.Environment['Logging__LogLevel__Default'] = 'Warning'
    $server = [System.Diagnostics.Process]::Start($startInfo)

    $healthy = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($server.HasExited) { throw "Web server exited with code $($server.ExitCode)." }
        try {
            $health = Invoke-RestMethod "$script:baseUrl/api/health"
            if ($health.status -eq 'healthy') { $healthy = $true; break }
        } catch { Start-Sleep -Milliseconds 250 }
    }
    Assert-True $healthy 'Web health endpoint did not become ready.'

    $unauthorized = 0
    try { Invoke-WebRequest "$script:baseUrl/api/projects" -SkipHttpErrorCheck | ForEach-Object { $unauthorized = $_.StatusCode } } catch { }
    Assert-True ($unauthorized -eq 401) 'Protected API did not reject an unauthenticated request.'

    $index = Invoke-WebRequest "$script:baseUrl/"
    Assert-True ($index.StatusCode -eq 200 -and $index.Content.Contains('Vision Studio')) 'Web editor entry page is unavailable.'
    Assert-True ([string]$index.Headers['Content-Security-Policy'] -like "*default-src 'self'*") 'Security headers are missing.'

    $projectInfo = Invoke-Api POST '/api/projects' @{ name = 'Web 自动化测试' }
    $projectId = $projectInfo.id
    $catalog = Invoke-Api GET "/api/projects/$projectId/catalog"
    Assert-True ($catalog.Count -ge 100) "Operator catalog is unexpectedly small: $($catalog.Count)."

    $upload = Invoke-RestMethod -Method Post -Uri "$script:baseUrl/api/projects/$projectId/images" -Headers @{ 'X-VisionStudio-Token' = $token } -Form @{ file = Get-Item -LiteralPath $sample }
    Assert-True ($upload.path -like 'images/input/*') 'Uploaded image was not stored under images/input.'

    $mutation = @{
        requestId = [Guid]::NewGuid().ToString('N')
        expectedRevision = 0
        summary = 'Web automated execution chain'
        edits = @(
            @{ kind = 'addNode'; nodeId = 'load'; operatorType = 'Image.Load'; x = 80; y = 100 }
            @{ kind = 'setParameter'; nodeId = 'load'; parameter = 'path'; value = $upload.path }
            @{ kind = 'addNode'; nodeId = 'gray'; operatorType = 'Image.Gray'; x = 360; y = 100 }
            @{ kind = 'connect'; fromNode = 'load'; fromPort = 'Image'; toNode = 'gray'; toPort = 'Image' }
        )
    }
    $mutated = Invoke-Api POST "/api/projects/$projectId/mutations" $mutation
    Assert-True ($mutated.revision -eq 1 -and $mutated.workflow.nodes.Count -eq 2) 'Workflow mutation did not persist two nodes.'

    $execution = Invoke-Api POST "/api/projects/$projectId/execute" @{ requestId = [Guid]::NewGuid().ToString('N'); targetNodeId = 'gray' }
    Assert-True ($execution.success -and $execution.outputs.Count -ge 1) 'Image workflow execution failed.'
    $artifactPath = $execution.outputs[0].artifactPath
    Assert-True (![string]::IsNullOrWhiteSpace($artifactPath)) 'Execution did not return a preview artifact.'
    $artifact = Invoke-WebRequest "$script:baseUrl/api/projects/$projectId/files?path=$([Uri]::EscapeDataString($artifactPath))" -Headers @{ 'X-VisionStudio-Token' = $token }
    Assert-True ($artifact.RawContentLength -gt 100) 'Execution preview artifact is empty.'

    $export = Invoke-Api POST "/api/projects/$projectId/export"
    Assert-True ($export.path -eq 'output/GeneratedVisionAlgorithm.cs') 'C# export path is incorrect.'
    $code = Invoke-WebRequest "$script:baseUrl/api/projects/$projectId/files?path=output%2FGeneratedVisionAlgorithm.cs" -Headers @{ 'X-VisionStudio-Token' = $token }
    Assert-True ($code.Content.Contains('GeneratedVisionAlgorithm')) 'Generated C# output is invalid.'

    $undo = Invoke-Api POST "/api/projects/$projectId/undo"
    Assert-True ($undo.revision -eq 2 -and $undo.workflow.nodes.Count -eq 0) 'Undo did not restore the previous workflow.'
    Write-Output 'Web dimension passed: auth, static UI, project, upload, workflow, execution, artifact, export, undo.'
}
finally {
    if ($null -ne $server -and !$server.HasExited) {
        $server.Kill($true)
        $server.WaitForExit(5000) | Out-Null
    }
    $fullTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    $safePrefix = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot 'artifacts')).TrimEnd('\') + '\'
    if ($fullTestRoot.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase) -and [System.IO.Path]::GetFileName($fullTestRoot).StartsWith('web-automated-test-')) {
        if (Test-Path -LiteralPath $fullTestRoot) { Remove-Item -LiteralPath $fullTestRoot -Recurse -Force }
    }
}
