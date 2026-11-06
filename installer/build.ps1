# Builds dist\Flyleaf-Setup.exe, the one file to share. It installs the one-app version per user, with no admin rights.
# Needs the .NET 10 SDK and the Visual Studio C++ build tools.
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
$layout = Join-Path $env:TEMP 'flyleaf-setup-layout'
$payload = Join-Path $env:TEMP 'flyleaf-setup-payload'

function Reset-Folder([string]$path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory $path | Out-Null
}

function Sign([string]$file) {
    if (-not $CertThumbprint) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw 'signtool.exe was not found. Install the Windows SDK.' }
    & $signtool.FullName sign /sha1 $CertThumbprint /fd sha256 /tr $TimestampUrl /td sha256 $file | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "Signing $file failed." }
}

# FlySetup is built twice with different embedded resources, so never reuse the previous build's output.
function Publish-FlySetup([string]$out, [string[]]$extraArgs) {
    foreach ($folder in 'bin', 'obj') {
        $path = Join-Path $root "src\FlySetup\$folder"
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }

    Reset-Folder $out
    & dotnet publish (Join-Path $root 'src\FlySetup\FlySetup.csproj') -c Release -o $out @extraArgs | Write-Host
    if ($LASTEXITCODE -ne 0) { throw 'Publishing FlySetup failed.' }
}

if (Test-Path $dist) { Remove-Item (Join-Path $dist 'Flyleaf-Setup.exe') -Force -ErrorAction SilentlyContinue } else { New-Item -ItemType Directory $dist | Out-Null }
Reset-Folder $layout
Reset-Folder $payload

# A normal folder install, like the Store package, so that the app never unpacks itself to a temp folder at run time.
& dotnet publish (Join-Path $root 'src\FlyleafStore\FlyleafStore.csproj') -c Release --self-contained true -o $layout | Write-Host
if ($LASTEXITCODE -ne 0) { throw 'Publishing Flyleaf failed.' }
Get-ChildItem $layout -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem $layout -Filter '*.xml' | Remove-Item -Force

$uninstallerOut = Join-Path $env:TEMP 'flyleaf-publish\Uninstaller'
Publish-FlySetup $uninstallerOut @()
Copy-Item (Join-Path $uninstallerOut 'Flyleaf-Setup.exe') (Join-Path $layout 'Uninstall-Flyleaf.exe')

# Our own files are signed before packing, because Windows checks each one again once it is installed.
foreach ($name in 'Flyleaf.exe', 'Flyleaf.dll', 'Flyleaf.Core.dll', 'Uninstall-Flyleaf.exe') { Sign (Join-Path $layout $name) }

Compress-Archive -Path (Join-Path $layout '*') -DestinationPath (Join-Path $payload 'Flyleaf.zip') -CompressionLevel Optimal

$setupOut = Join-Path $env:TEMP 'flyleaf-publish\Setup'
Publish-FlySetup $setupOut @("-p:PayloadDir=$payload")
$setup = Join-Path $dist 'Flyleaf-Setup.exe'
Copy-Item (Join-Path $setupOut 'Flyleaf-Setup.exe') $setup
Sign $setup

Get-Item $setup | Select-Object Name, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 1) } }, @{ n = 'Signature'; e = { (Get-AuthenticodeSignature $_.FullName).Status } } | Format-Table -AutoSize
