[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BuildCommon.ps1')
$root=Get-BuildRoot
function Assert-BuildTest([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message }; Write-Host ('PASS '+$Message) }
Assert-BuildTest ([Console]::OutputEncoding.CodePage -eq 65001 -and $OutputEncoding.CodePage -eq 65001) 'Native output decoder and pipeline encoding both use UTF-8'
$nativeProbe='[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false); [Console]::WriteLine(([string][char]0x9002)+[char]0x7528+[char]0x4e8e+" MSBuild "+[char]0x7248+[char]0x672c)'
$nativeProbeEncoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($nativeProbe))
$nativeChinese=& powershell.exe -NoProfile -EncodedCommand $nativeProbeEncoded
Assert-BuildTest ($nativeChinese -eq '适用于 MSBuild 版本') 'UTF-8 Chinese native process output is decoded without mojibake in Windows PowerShell'
foreach ($scriptFile in @((Get-ChildItem -LiteralPath $root -Filter '*.ps1' -File)) + @((Get-ChildItem -LiteralPath (Join-Path $root 'tools') -Filter '*.ps1' -File))) {
    $bytes=[IO.File]::ReadAllBytes($scriptFile.FullName)
    Assert-BuildTest ($bytes.Length -ge 3 -and $bytes[0] -eq 0xef -and $bytes[1] -eq 0xbb -and $bytes[2] -eq 0xbf) ('Windows PowerShell UTF-8 BOM '+$scriptFile.Name)
}
foreach ($relative in @('build.ps1','build-gen2.ps1','sign-gen2.ps1','build-app.ps1','build-gui.ps1','build-dma-driver.ps1','build-release.ps1','update-release-hashes.ps1','tools\BuildCommon.ps1','tools\generate-certificate-policy.ps1','tools\ConsoleEncoding.ps1')) {
    $tokens=$null; $errors=$null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $root $relative),[ref]$tokens,[ref]$errors) | Out-Null
    Assert-BuildTest ($errors.Count -eq 0) ('PowerShell parses '+$relative)
}
$directory=Join-Path $root ('.build\script-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
try {
    $rejected=$false
    try { Get-BuildCertificate $directory | Out-Null } catch { $rejected=$true }
    Assert-BuildTest $rejected 'Empty certificate directory is rejected'
    $fixtureCertificate=Get-BuildCertificate
    Copy-Item -LiteralPath $fixtureCertificate.Path -Destination (Join-Path $directory 'first.cer')
    $selected=Get-BuildCertificate $directory
    Assert-BuildTest ($selected.FileName -eq 'first.cer' -and $selected.Thumbprint -eq $fixtureCertificate.Thumbprint -and $selected.Hash -eq $fixtureCertificate.Hash) 'Certificate metadata is discovered from the actual file, independent of its name'
    Copy-Item -LiteralPath $fixtureCertificate.Path -Destination (Join-Path $directory 'second.cer')
    $rejected=$false
    try { Get-BuildCertificate $directory | Out-Null } catch { $rejected=$true }
    Assert-BuildTest $rejected 'Multiple certificates are rejected without an arbitrary choice'
    $callerHash='0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF'
    $callerPolicy=Get-CallerPolicyContent $callerHash
    Assert-BuildTest ($callerPolicy.Contains($callerHash) -and $callerPolicy.Contains('0x01,0x23,0x45,0x67,0x89,0xAB,0xCD,0xEF')) 'Caller policy encodes the full SHA256 in text and bytes'
    $rejected=$false
    try { Get-CallerPolicyContent 'bad' | Out-Null } catch { $rejected=$true }
    Assert-BuildTest $rejected 'Malformed caller hash is rejected'
    $policyFile=Join-Path $directory 'policy.sys'
    [IO.File]::WriteAllBytes($policyFile,[Text.Encoding]::ASCII.GetBytes("PE-fixture CMP90HX_CALLER_SHA256=$callerHash`0"))
    Assert-DriverCallerPolicy $policyFile $callerHash
    Assert-BuildTest $true 'Embedded matching caller policy is accepted'
    $rejected=$false
    try { Assert-DriverCallerPolicy $policyFile ('F'*64) } catch { $rejected=$true }
    Assert-BuildTest $rejected 'Stale driver and changed worker are rejected'
    [IO.File]::WriteAllBytes($policyFile,[Text.Encoding]::ASCII.GetBytes('old-driver-no-policy'))
    $rejected=$false
    try { Assert-DriverCallerPolicy $policyFile $callerHash } catch { $rejected=$true }
    Assert-BuildTest $rejected 'Old driver without caller enforcement is rejected'
    # Minimal PE32+ with a body ending off the Authenticode alignment boundary.
    $unsigned=New-Object byte[] 513
    [Array]::Copy([BitConverter]::GetBytes([uint16]0x5a4d),0,$unsigned,0,2)
    [Array]::Copy([BitConverter]::GetBytes([int]64),0,$unsigned,60,4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]0x4550),0,$unsigned,64,4)
    [Array]::Copy([BitConverter]::GetBytes([uint16]0x20b),0,$unsigned,88,2)
    $unsigned[400]=90; $unsigned[512]=72
    $source=Join-Path $directory 'unsigned.sys'
    [IO.File]::WriteAllBytes($source,$unsigned)
    $pauseBuild=Join-Path $directory 'build'
    New-Item -ItemType Directory -Path $pauseBuild | Out-Null
    $pauseRuntime=Join-Path $pauseBuild 'CMP90HXUnlocker.exe'
    [IO.File]::WriteAllBytes($pauseRuntime,$unsigned)
    $pauseHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    & {
        function Get-BuildRoot { return $directory }
        function Get-Gen2ProjectOutput { return $source }
        function Assert-Gen2Validated { return $pauseHash }
        $script:PausePrompts=0
        function Read-Host { param([string]$Prompt) $script:PausePrompts++; return '' }
        Wait-Gen2OptionalSigning -NonInteractive
        Assert-BuildTest ($script:PausePrompts -eq 0) 'Noninteractive release skips optional signing without a prompt'
        Wait-Gen2OptionalSigning
        Assert-BuildTest ($script:PausePrompts -eq 1 -and (Get-FileHash -LiteralPath $pauseRuntime -Algorithm SHA256).Hash -eq $pauseHash) 'Enter continues with the unchanged unsigned Gen2'
        function Read-Host {
            param([string]$Prompt)
            $changed=[IO.File]::ReadAllBytes($pauseRuntime);$changed[400]=91
            [IO.File]::WriteAllBytes($pauseRuntime,$changed)
            return ''
        }
        $rejected=$false
        try { Wait-Gen2OptionalSigning } catch { $rejected=$true }
        Assert-BuildTest $rejected 'Signing pause rejects executable content changes before generating a whitelist'
    }
    $expected=Get-BuildPayloadHash $source
    $signed=New-Object byte[] 528
    [Array]::Copy($unsigned,$signed,$unsigned.Length)
    [Array]::Copy([BitConverter]::GetBytes([uint32]1234),0,$signed,152,4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]520),0,$signed,232,4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]8),0,$signed,236,4)
    $signed[520]=42
    $candidate=Join-Path $directory 'signed.sys'
    [IO.File]::WriteAllBytes($candidate,$signed)
    & {
        $script:GuiPausePrompts=0
        function Read-Host { param([string]$Prompt) $script:GuiPausePrompts++; return '' }
        Wait-GuiOptionalSigning $source -NonInteractive
        Assert-BuildTest ($script:GuiPausePrompts -eq 0) 'Noninteractive GUI build skips signing without a prompt'
        Wait-GuiOptionalSigning $source
        Assert-BuildTest ($script:GuiPausePrompts -eq 1) 'Enter continues with the unchanged unsigned GUI'
        function Read-Host { param([string]$Prompt) [IO.File]::WriteAllBytes($source,$signed); return '' }
        function Get-AuthenticodeSignature { param([string]$LiteralPath) [pscustomobject]@{Status='Valid'} }
        Wait-GuiOptionalSigning $source
        Assert-BuildTest ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $candidate).Hash) 'GUI signing accepts changed signature bytes with the same executable body (mock signature trust)'
        [IO.File]::WriteAllBytes($source,$unsigned)
        function Get-AuthenticodeSignature { param([string]$LiteralPath) [pscustomobject]@{Status='HashMismatch'} }
        $rejected=$false
        try { Wait-GuiOptionalSigning $source } catch { $rejected=$true }
        Assert-BuildTest $rejected 'GUI signing rejects an invalid changed signature'
        [IO.File]::WriteAllBytes($source,$unsigned)
        function Read-Host { param([string]$Prompt) $modified=[byte[]]$unsigned.Clone();$modified[400]=91;[IO.File]::WriteAllBytes($source,$modified); return '' }
        $rejected=$false
        try { Wait-GuiOptionalSigning $source } catch { $rejected=$true }
        Assert-BuildTest $rejected 'GUI signing rejects changed executable content'
        function Read-Host { param([string]$Prompt) return 'q' }
        $rejected=$false
        try { Wait-GuiOptionalSigning $candidate } catch { $rejected=$true }
        Assert-BuildTest $rejected 'GUI signing can cancel before packaging'
        [IO.File]::WriteAllBytes($source,$unsigned)
    }
    Assert-BuildTest ((Get-BuildPayloadHash $candidate) -eq $expected) 'PE checksum, certificate table and seven alignment bytes do not change the body hash'
    $signed[400]=91
    [IO.File]::WriteAllBytes($candidate,$signed)
    Assert-BuildTest ((Get-BuildPayloadHash $candidate) -ne $expected) 'Changed executable content is detected even with a certificate table'
    $signed[400]=90
    [IO.File]::WriteAllBytes($candidate,$signed)
    # Mock only certificate status; body comparison uses actual test files.
    function Get-AuthenticodeSignature { param([string]$LiteralPath) [pscustomobject]@{Status=$script:MockSignatureStatus} }
    $script:MockSignatureStatus='Valid'
    Assert-BuildTest ((Wait-BuildSignedDriver $expected $candidate -NonInteractive) -eq $candidate) 'Noninteractive driver handoff accepts the same signed body'
    $script:MockSignatureStatus='NotSigned'; $rejected=$false
    try { Wait-BuildSignedDriver $expected $candidate -NonInteractive | Out-Null } catch { $rejected=$true }
    Assert-BuildTest $rejected 'Noninteractive handoff rejects an unsigned driver without prompting'
    $script:MockSignatureStatus='Valid'; $signed[400]=91
    [IO.File]::WriteAllBytes($candidate,$signed); $rejected=$false
    try { Wait-BuildSignedDriver $expected $candidate -NonInteractive | Out-Null } catch { $rejected=$true }
    Assert-BuildTest $rejected 'Noninteractive handoff rejects a signed driver from a different build'
    Remove-Item Function:\Get-AuthenticodeSignature
    [Array]::Copy([BitConverter]::GetBytes([uint32]9999),0,$signed,236,4)
    [IO.File]::WriteAllBytes($candidate,$signed); $rejected=$false
    try { Get-BuildPayloadHash $candidate | Out-Null } catch { $rejected=$true }
    Assert-BuildTest $rejected 'Out-of-bounds certificate tables are rejected'
    foreach ($relative in @('.build/local.json','.build/temporary/private.pem','temporary.pfx','5cca7e4891a7ed8f653b3720ece7a6f7.zip')) {
        & git -C $root check-ignore --quiet -- $relative
        Assert-BuildTest ($LASTEXITCODE -eq 0) ('Git ignores signing material/config '+$relative)
    }
    Assert-BuildTest (Test-Path -LiteralPath (Join-Path $root 'src\CMP90HXUnlockProgram.csproj')) 'Renamed Gen2 project is selected'
    Write-Host 'BUILD_SCRIPT_TESTS_PASSED (no GPU access or driver installation)'
} finally {
    if (Test-Path Function:\Get-AuthenticodeSignature) { Remove-Item Function:\Get-AuthenticodeSignature }
    $resolved=[IO.Path]::GetFullPath($directory)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath((Join-Path $root '.build'))+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    foreach ($name in @('unsigned.sys','signed.sys','policy.sys','first.cer','second.cer')) {
        $path=Join-Path $resolved $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    if (Test-Path -LiteralPath (Join-Path $resolved 'build\CMP90HXUnlocker.exe')) { Remove-Item -LiteralPath (Join-Path $resolved 'build\CMP90HXUnlocker.exe') }
    if (Test-Path -LiteralPath (Join-Path $resolved 'build')) { Remove-Item -LiteralPath (Join-Path $resolved 'build') }
    Remove-Item -LiteralPath $resolved
}
