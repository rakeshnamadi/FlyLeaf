# Builds dist\store\Flyleaf.msix for upload to Partner Center. The Store signs the package when it publishes it.
# The three identity values come from Partner Center > your app > Product identity. The defaults are only for test builds.
param(
    [string]$IdentityName = 'Flyleaf.Dev',
    [string]$Publisher = 'CN=FlyleafDev',
    [string]$PublisherDisplayName = 'Flyleaf',
    [string]$Version = '1.0.0.0'
)
$ErrorActionPreference = 'Stop'
# Native AOT finds the C++ linker through vswhere.exe, which a window opened before the tools were installed can't see.
$env:PATH = "$env:ProgramFiles\dotnet;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\FlyleafStore'
$output = Join-Path $root 'dist\store'
$layout = Join-Path $env:TEMP 'flyleaf-store-layout'

foreach ($folder in $output, $layout) {
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
    New-Item -ItemType Directory $folder | Out-Null
}

# Not single-file: the Store's signature covers every file in the package, which wouldn't include files extracted at run time.
& dotnet publish (Join-Path $project 'FlyleafStore.csproj') -c Release --self-contained true -o $layout | Write-Host
if ($LASTEXITCODE -ne 0) { throw 'Publishing FlyleafStore failed.' }

$menuOut = Join-Path $env:TEMP 'flyleaf-publish\FlyleafMenu'
if (Test-Path $menuOut) { Remove-Item $menuOut -Recurse -Force }
& dotnet publish (Join-Path $root 'src\FlyleafMenu\FlyleafMenu.csproj') -c Release -o $menuOut | Write-Host
if ($LASTEXITCODE -ne 0) { throw 'Publishing FlyleafMenu failed.' }
Copy-Item (Join-Path $menuOut 'FlyleafMenu.dll') $layout

Get-ChildItem $layout -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem $layout -Filter '*.xml' | Remove-Item -Force
Copy-Item (Join-Path $project 'Package\Assets') $layout -Recurse

$manifest = (Get-Content (Join-Path $project 'Package\AppxManifest.xml') -Raw).
    Replace('$IdentityName$', $IdentityName).
    Replace('$Publisher$', $Publisher).
    Replace('$PublisherDisplayName$', $PublisherDisplayName).
    Replace('$Version$', $Version)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" | Sort-Object FullName | Select-Object -Last 1
if (-not $makeappx) { throw 'makeappx.exe was not found. Install the Windows SDK.' }

$package = Join-Path $output 'Flyleaf.msix'
& $makeappx.FullName pack /o /d $layout /p $package | Write-Host
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed.' }

Get-Item $package | Select-Object Name, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
