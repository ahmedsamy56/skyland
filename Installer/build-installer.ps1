<#
.SYNOPSIS
  Builds SkyLand-<version>.msi and its SHA-256 file into ..\dist.

.DESCRIPTION
  1. dotnet publish (self-contained single file) into a staging folder
  2. optional code signing of SkyLand.exe (only if -CertPath is given)
  3. wix build  ->  SkyLand-<version>.msi
  4. optional signing of the MSI
  5. SkyLand-<version>.msi.sha256   (the in-app updater refuses an installer without a matching hash)

  The version comes from <Version> in DynamicIsland\DynamicIsland.csproj. Bump it there, then run this script.
  Pass -Version only for test builds.

  Signing: nothing secret is stored anywhere. Give -CertPath (a .pfx) and the password is requested at the prompt,
  or set the SKYLAND_CERT_PASSWORD environment variable for the session.

.EXAMPLE
  .\build-installer.ps1
  .\build-installer.ps1 -CertPath C:\keys\skyzack.pfx
#>
param(
    [string]$Version,
    [string]$CertPath,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "DynamicIsland\DynamicIsland.csproj"
$dist = Join-Path $root "dist"
$stage = Join-Path $root "dist\stage"

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' must look like 1.2.3" }
Write-Host "Building SkyLand $Version" -ForegroundColor Cyan

# --- signing helper ---------------------------------------------------------------------------------------------
function Get-SignTool {
    $found = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw "signtool.exe not found. Install the Windows 10/11 SDK (Signing Tools) to sign." }
    $found.FullName
}

$password = $null
if ($CertPath) {
    if (-not (Test-Path $CertPath)) { throw "Certificate not found: $CertPath" }
    $password = $env:SKYLAND_CERT_PASSWORD
    if (-not $password) {
        $secure = Read-Host "Certificate password" -AsSecureString
        $password = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }
}
function Sign($file) {
    if (-not $CertPath) { return }
    & (Get-SignTool) sign /f $CertPath /p $password /fd SHA256 /tr $TimestampUrl /td SHA256 /d "SkyLand" $file
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $file" }
}

# --- publish ----------------------------------------------------------------------------------------------------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

dotnet publish (Join-Path $root "DynamicIsland") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The publish step also copies the hook tool next to the exe; the MSI takes it from the hook folder only.
Remove-Item (Join-Path $stage "island-hook.*") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "DynamicIsland\Assets\skyland.ico") $stage -Force
$hookDir = Join-Path $stage "hook"
if (-not (Test-Path (Join-Path $hookDir "island-hook.exe"))) { throw "hook\island-hook.exe is missing from the publish output" }

Sign (Join-Path $stage "SkyLand.exe")

# --- msi --------------------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $dist | Out-Null
$msi = Join-Path $dist "SkyLand-$Version.msi"
if (Test-Path $msi) { Remove-Item $msi -Force }

wix build (Join-Path $PSScriptRoot "Package.wxs") -arch x64 -ext WixToolset.Util.wixext `
    -d Version=$Version -d SourceDir=$stage -d HookDir=$hookDir -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

Sign $msi

# --- checksum ---------------------------------------------------------------------------------------------------
$hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLower()
$sumFile = "$msi.sha256"
[IO.File]::WriteAllText($sumFile, "$hash  $(Split-Path $msi -Leaf)`n", (New-Object Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "Built:" -ForegroundColor Green
Write-Host "  $msi  ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
Write-Host "  $sumFile"
Write-Host "  SHA-256: $hash"
Write-Host "  Signed:  $([bool]$CertPath)"
Write-Host ""
Write-Host "To publish (after 'gh auth login'):" -ForegroundColor Cyan
Write-Host "  gh release create v$Version `"$msi`" `"$sumFile`" --repo cbsajan/skyland --title `"SkyLand $Version`" --notes `"...`""
