$ErrorActionPreference = 'Stop'

$version = '0.3.0'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The built-in Windows C# compiler was not found.'
}

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $projectDir 'CodexQuotaLocal.cs'
$guiOutput = Join-Path $projectDir 'CodexQuotaLocal.exe'
$cliOutput = Join-Path $projectDir 'CodexQuotaLocalCli.exe'
$distDir = Join-Path $projectDir 'dist'
$packageName = "CodexQuotaLocal-v$version-win-x64-portable"
$stageDir = Join-Path $distDir $packageName
$zipPath = Join-Path $distDir "$packageName.zip"

& $compiler /nologo /codepage:65001 /optimize+ /target:winexe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Windows.Forms.dll `
    "/out:$guiOutput" `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "GUI compilation failed with exit code $LASTEXITCODE"
}

& $compiler /nologo /codepage:65001 /optimize+ /target:exe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Windows.Forms.dll `
    "/out:$cliOutput" `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "CLI compilation failed with exit code $LASTEXITCODE"
}

if (Test-Path -LiteralPath $stageDir) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir | Out-Null

Copy-Item -LiteralPath $guiOutput -Destination $stageDir
Copy-Item -LiteralPath $cliOutput -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'README.md') -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'PRIVACY.md') -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'SECURITY.md') -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'CHANGELOG.md') -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'RELEASE_NOTES_v0.3.0.md') -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'LICENSE') -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'Run-Offline.cmd') -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'Run-With-Radar.cmd') -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'Snapshot-Offline.cmd') -Destination $stageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'Snapshot-With-Radar.cmd') -Destination $stageDir

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Output "Built: $guiOutput"
Write-Output "Built: $cliOutput"
Write-Output "Packaged: $zipPath"
