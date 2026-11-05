# Installs a test build of dist\store\Flyleaf.msix on this PC, so the installed-only features can be tried before the Store.
# It signs the package with a throwaway self-signed certificate. Trusting that certificate is a machine-wide
# security change, so Windows asks for administrator approval. Undo everything afterwards with -Remove.
param([switch]$Remove)
$ErrorActionPreference = 'Stop'

$subject = 'CN=FlyleafDev'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$package = Join-Path $root 'dist\store\Flyleaf.msix'
$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -EQ $subject | Select-Object -First 1

if ($Remove) {
    Get-AppxPackage -Name 'Flyleaf.Dev' | Remove-AppxPackage
    if ($certificate) {
        Start-Process certutil.exe -ArgumentList '-delstore', 'TrustedPeople', $certificate.Thumbprint -Verb RunAs -Wait
        Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)"
    }
    Write-Host 'The test build and its certificate have been removed.'
    return
}

if (-not $certificate) {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature -NotAfter (Get-Date).AddMonths(3) -TextExtension @('2.5.29.19={text}')
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName | Select-Object -Last 1
& $signtool.FullName sign /sha1 $certificate.Thumbprint /fd sha256 $package
if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }

$publicCertificate = Join-Path $env:TEMP 'flyleaf-test.cer'
Export-Certificate -Cert $certificate -FilePath $publicCertificate | Out-Null
$trust = Start-Process certutil.exe -ArgumentList '-addstore', 'TrustedPeople', "`"$publicCertificate`"" -Verb RunAs -Wait -PassThru
Remove-Item $publicCertificate
if ($trust.ExitCode -ne 0) { throw 'The certificate could not be trusted.' }

Add-AppxPackage -Path $package
Write-Host 'Flyleaf is installed. Find it in the Start menu. To undo all of this, run this script again with -Remove.'
