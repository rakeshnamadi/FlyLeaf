# Builds dist\Flyleaf-Setup.exe, the one file to share. Needs the .NET 10 SDK and the Visual Studio C++ build tools.
#
# To sign, install your code-signing certificate and pass its thumbprint:
#   .\installer\build.ps1 -CertThumbprint <thumbprint>
param(
    [string]$CertThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'

# Native AOT finds the C++ linker through vswhere.exe, which a window opened before the tools were installed can't see.
$env:PATH = "$env:ProgramFiles\dotnet;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dist = Join-Path $root 'dist'
$payload = Join-Path $dist 'payload'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $payload | Out-Null

function Publish([string]$project, [string[]]$extraArgs) {
    $out = Join-Path $env:TEMP "flyleaf-publish\$project"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & dotnet publish (Join-Path $root "src\$project\$project.csproj") -c Release -o $out @extraArgs | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
    return $out
}

function Sign([string]$file) {
    if (-not $CertThumbprint) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw 'signtool.exe was not found. Install the Windows SDK.' }
    & $signtool.FullName sign /sha1 $CertThumbprint /fd sha256 /tr $TimestampUrl /td sha256 $file | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "Signing $file failed." }
}

# FlySetup is built twice with different embedded resources, so never reuse the previous build's output.
function Clean-FlySetup {
    foreach ($folder in 'bin', 'obj') {
        $path = Join-Path $root "src\FlySetup\$folder"
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
}

$tray = Publish 'Flyleaf' @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
Copy-Item (Join-Path $tray 'Flyleaf.exe') $payload

$watcher = Publish 'FlyWatcher' @()
Copy-Item (Join-Path $watcher 'FlyWatcher.exe') $payload

$shell = Publish 'FlyShell' @()
Copy-Item (Join-Path $shell 'FlyShell.dll') $payload

Clean-FlySetup
$uninstaller = Publish 'FlySetup' @()
Copy-Item (Join-Path $uninstaller 'Flyleaf-Setup.exe') (Join-Path $payload 'Uninstall-Flyleaf.exe')

# Everything is signed before being embedded, because Windows checks each file again once it is installed.
Get-ChildItem $payload -File | ForEach-Object { Sign $_.FullName }

Clean-FlySetup
$setup = Publish 'FlySetup' @("-p:PayloadDir=$payload")
Copy-Item (Join-Path $setup 'Flyleaf-Setup.exe') $dist
Sign (Join-Path $dist 'Flyleaf-Setup.exe')

Get-ChildItem $dist -File -Recurse | Select-Object Name, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 1) } }, @{ n = 'Signature'; e = { (Get-AuthenticodeSignature $_.FullName).Status } } | Format-Table -AutoSize
