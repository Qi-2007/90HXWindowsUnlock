[CmdletBinding()]
param(
    [string]$CertificatePath,
    [Security.SecureString]$CertificatePassword,
    [string]$TimestampUrl,
    [string]$SignTool,
    [switch]$ChooseCertificate,
    [switch]$NonInteractive
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\BuildCommon.ps1')
$validated=Assert-Gen2Validated
$exe=Get-Gen2ProjectOutput
if (!(Test-Path -LiteralPath $exe) -or (Get-FileHash -LiteralPath $exe).Hash -ne $validated) { throw 'Project output and validated Gen2 differ. Run build.ps1 first.' }
$CertificatePath=Select-BuildCertificate $CertificatePath -Choose:$ChooseCertificate -NonInteractive:$NonInteractive
Protect-BuildCertificatePath $CertificatePath
if ($null -eq $CertificatePassword) { $CertificatePassword=Read-BuildBundlePassword $CertificatePath }
$config=Read-BuildConfig
if (!$TimestampUrl) { $TimestampUrl=$config.TimestampUrl }
if (!$TimestampUrl) { $TimestampUrl='http://timestamp.digicert.com' }
if ($TimestampUrl -notmatch '^https?://') { throw 'TimestampUrl must be an HTTP/HTTPS RFC3161 timestamp server.' }
$SignTool=Resolve-BuildSignTool $SignTool
$bytes=Read-BuildPfx $CertificatePath
Save-BuildSigningConfig $CertificatePath $TimestampUrl
$probe=$null; $persistent=$null; $store=$null
$added=New-Object 'System.Collections.Generic.List[string]'
$stage=Join-Path $PSScriptRoot ('build\gen2-sign-'+[guid]::NewGuid().ToString('N')+'.exe')
try {
    try { $probe=Import-BuildSigningPfx $bytes $CertificatePassword ([Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet) }
    catch {
        if ($null -ne $CertificatePassword -or $NonInteractive) { throw 'PFX could not be opened. Pass its password as -CertificatePassword (SecureString).' }
        $CertificatePassword=Read-Host 'PFX password (not saved)' -AsSecureString
        $probe=Import-BuildSigningPfx $bytes $CertificatePassword ([Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    }
    $leaves=@($probe | Where-Object HasPrivateKey)
    if ($leaves.Count -ne 1) { throw 'PFX must contain exactly one signing certificate with a private key.' }
    $leaf=$leaves[0]
    # Check EKU explicitly; an absent EKU means unrestricted usage.
    $usage=@($leaf.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($usage.Count -and !@($usage[0].EnhancedKeyUsages | Where-Object Value -EQ '1.3.6.1.5.5.7.3.3').Count) { throw 'Certificate lacks EXE Code Signing EKU (1.3.6.1.5.5.7.3.3). A kernel-driver-only certificate cannot be used for Gen2 /pa verification; choose another PFX.' }
    if ((Get-Date) -lt $leaf.NotBefore -or (Get-Date) -gt $leaf.NotAfter) { throw 'Signing certificate is outside its validity period.' }
    $store=New-Object Security.Cryptography.X509Certificates.X509Store('My','CurrentUser')
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $existing=@($store.Certificates | Where-Object Thumbprint -EQ $leaf.Thumbprint)
    if ($existing.Count -and !$existing[0].HasPrivateKey) { throw 'An existing public-only certificate has the same thumbprint. Resolve its private key before signing.' }
    if (!$existing.Count) {
        $flags=[Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet -bor [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet
        $persistent=Import-BuildSigningPfx $bytes $CertificatePassword $flags
        foreach ($certificate in $persistent) {
            if (!@($store.Certificates | Where-Object Thumbprint -EQ $certificate.Thumbprint).Count) {
                $store.Add($certificate); $added.Add($certificate.Thumbprint)
            }
        }
    }
    Copy-Item -LiteralPath $exe -Destination $stage
    # Only a thumbprint goes on the command line; no PFX password is exposed.
    & $SignTool sign /fd SHA256 /s My /sha1 $leaf.Thumbprint /tr $TimestampUrl /td SHA256 $stage | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Gen2 signing failed: $LASTEXITCODE" }
    & $SignTool verify /pa $stage | Out-Host
    if ($LASTEXITCODE -ne 0 -or (Get-AuthenticodeSignature -LiteralPath $stage).Status -ne 'Valid') { throw 'Gen2 Authenticode verification failed.' }
    if ((Get-BuildPayloadHash $exe) -ne (Get-BuildPayloadHash $stage)) { throw 'Signing changed the Gen2 executable body.' }
    # Signed bytes are tested again; never update the validation stamp blindly.
    & $stage self-test | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Signed Gen2 self-tests failed.' }
    $core=Join-Path $PSScriptRoot 'vendor\nvpermissive-dist-469dc0c\obj\nvpermissive-core.o'
    if (Test-Path -LiteralPath $core) {
        & $stage core-test --core $core | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Signed Gen2 core tests failed.' }
    }
    Move-Item -LiteralPath $stage -Destination $exe -Force
    $runtime=$exe
    $hash=(Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'build\CMP90HXUnlocker.validated.sha256') -Value $hash -Encoding ASCII
    Save-BuildSigningConfig $CertificatePath $TimestampUrl
    Write-Host "SIGNED_GEN2_READY $runtime"
    [pscustomobject]@{Gen2Path=$runtime;ProjectOutput=$exe;Gen2Hash=$hash}
} finally {
    if ($store) { $store.Close() }
    foreach ($thumbprint in $added) {
        $path='Cert:\CurrentUser\My\'+$thumbprint
        if (Test-Path -LiteralPath $path) {
            if ((Get-Item -LiteralPath $path).HasPrivateKey) {
                try { Remove-Item -LiteralPath $path -DeleteKey }
                catch {
                    if ($_.Exception.NativeErrorCode -ne -2146893802) { throw }
                    # Some providers have no persisted keyset; remove their stale cert.
                    Remove-Item -LiteralPath $path
                }
            }
            else { Remove-Item -LiteralPath $path }
        }
    }
    foreach ($collection in @($probe,$persistent)) { if ($collection) { foreach ($certificate in $collection) { $certificate.Dispose() } } }
    if ($bytes) { [Array]::Clear($bytes,0,$bytes.Length) }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Force }
}
