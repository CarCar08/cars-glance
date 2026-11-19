<#
  Builds a release: the widget (self-contained, so no .NET install is needed) inside the single-file installer.

    powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 [-Version 0.9.0] [-Output artifacts]

  Produces, in the output folder (which must not exist yet, or be empty):
    CarsGlance-Setup-<version>.exe   the installer (the app and every library inside, one file)
    SHA256SUMS.txt                   its checksum, for the Releases page and for self-update
    release-notes.md                 this version's section of CHANGELOG.md

  Needs the .NET 10 SDK. No admin rights, no Visual Studio. Works the same on a developer PC and in GitHub
  Actions (.github\workflows\release.yml calls this script). It never deletes anything: debug symbols and other
  languages are switched off at build time, and the working files go to a fresh temp folder.
#>
param(
    [string]$Version,
    [string]$Output = 'artifacts',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# The version: -Version, else <Version> in Directory.Build.props.
if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
    $Version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$') { throw "Not a version: '$Version' (expected 1.2.3 or 1.2.3-rc.1)" }
# Assembly versions are four numbers; the full text goes in InformationalVersion.
$numeric = ($Version -replace '-.*$', '') + '.0'

if ($Output -notmatch '^artifacts(?:-[A-Za-z0-9_.-]+)?$') { throw 'Output must be a repository-root artifacts or artifacts-<name> folder.' }
$out = [IO.Path]::GetFullPath((Join-Path $root $Output))
if ((Test-Path $out) -and (Get-ChildItem $out -Force | Select-Object -First 1)) { throw "$out isn't empty: pick another -Output (e.g. artifacts-$Version)." }
New-Item -ItemType Directory -Force $out | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('CarsGlance-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Run($file, $arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file failed ($LASTEXITCODE)" }
}

Step "Building C.A.R.S. Glance $Version (self-contained)"
$app = Join-Path $work 'app'
Run 'dotnet' @('publish', 'src\Glance\Glance.csproj', '-c', $Configuration, '-o', $app, '--nologo', '-v', 'q',
    '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=none', '-p:GenerateDocumentationFile=false',
    "-p:Version=$numeric", "-p:InformationalVersion=$Version")
# The Settings window publishes into the same folder (same runtime files, so nothing is duplicated).
Run 'dotnet' @('publish', 'src\Glance.Settings\Glance.Settings.csproj', '-c', $Configuration, '-o', $app, '--nologo', '-v', 'q',
    '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=none', '-p:GenerateDocumentationFile=false',
    "-p:Version=$numeric", "-p:InformationalVersion=$Version")
foreach ($f in 'CarsGlance.exe', 'CarsGlance.Settings.exe', 'settings.default.json') {
    if (-not (Test-Path (Join-Path $app $f))) { throw "Missing $f in the publish output" }
}
# The licences travel with the program.
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $app 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $app 'THIRD-PARTY-NOTICES.md')

Step "Packing the payload"
$tool = Join-Path $work 'tool'
Run 'dotnet' @('publish', 'tools\Glance.ReleaseTool\Glance.ReleaseTool.csproj', '-c', $Configuration, '-o', $tool, '--nologo', '-v', 'q')
$payload = Join-Path $work 'payload'
Run 'dotnet' @((Join-Path $tool 'Glance.ReleaseTool.dll'), 'payload', $app, $Version, $payload)

Step "Building the installer"
$setupBuild = Join-Path $work 'setup'
Run 'dotnet' @('build', 'src\Glance.Setup\Glance.Setup.csproj', '-c', $Configuration, '-o', $setupBuild, '--nologo', '-v', 'q',
    "-p:Version=$numeric", "-p:InformationalVersion=$Version",
    "-p:PayloadZip=$(Join-Path $payload 'payload.zip')", "-p:PayloadJson=$(Join-Path $payload 'payload.json')")
$setupExe = Join-Path $out "CarsGlance-Setup-$Version.exe"
Copy-Item (Join-Path $setupBuild 'CarsGlance-Setup.exe') $setupExe

Step "Checksums and release notes"
Run 'dotnet' @((Join-Path $tool 'Glance.ReleaseTool.dll'), 'sums', (Join-Path $out 'SHA256SUMS.txt'), $setupExe)
Run 'dotnet' @((Join-Path $tool 'Glance.ReleaseTool.dll'), 'notes', (Join-Path $root 'CHANGELOG.md'), $Version, (Join-Path $out 'release-notes.md'))

# Optional signing: when a certificate is configured (GLANCE_SIGN_*), the installer is signed here, before its
# checksum is taken. Planned for 1.6 (SignPath for open source); until then it stays unsigned.
if ($env:GLANCE_SIGN_PFX) { Write-Warning 'GLANCE_SIGN_PFX is set but signing is not wired yet: the installer stays unsigned.' }

$size = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $setupExe ($size MB)  (working files: $work)" -ForegroundColor Green
Get-Content (Join-Path $out 'SHA256SUMS.txt')
