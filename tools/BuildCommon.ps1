# Shared by build scripts. Windows PowerShell 5.1 compatible; never prints secrets.
. (Join-Path $PSScriptRoot 'ConsoleEncoding.ps1')
function Get-BuildRoot { Split-Path -Parent $PSScriptRoot }

function Get-BuildCertificate([string]$Directory) {
    if (!$Directory) { $Directory=Join-Path (Get-BuildRoot) 'cert' }
    if (!(Test-Path -LiteralPath $Directory -PathType Container)) { throw "Certificate directory is missing: $Directory" }
    $files=@(Get-ChildItem -LiteralPath $Directory -File | Where-Object Extension -in @('.cer','.crt'))
    if ($files.Count -ne 1) { throw "Expected exactly one public .cer/.crt certificate in $Directory; found $($files.Count)." }
    $cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2($files[0].FullName)
    try {
        if ($cert.HasPrivateKey) { throw 'Only a public certificate may be included in the release.' }
        return [pscustomobject]@{Path=$files[0].FullName;SourceDirectory=$files[0].DirectoryName;FileName=$files[0].Name;Hash=(Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash;Thumbprint=$cert.Thumbprint;DisplayName=$cert.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false)}
    } finally { $cert.Dispose() }
}
function Write-CertificatePolicy([string]$Directory) {
    $cert=Get-BuildCertificate $Directory
    $lines=@('namespace CMP90HX.Control { internal static class CertificatePolicy {')
    foreach ($name in @('FileName','Hash','Thumbprint','DisplayName','SourceDirectory')) {
        $literal=ConvertTo-Json -InputObject ([string]$cert.$name) -Compress
        $lines+='internal const string '+$name+' = '+$literal+';'
    }
    $lines+='} }'
    $path=Join-Path (Get-BuildRoot) 'build\CertificatePolicy.cs'
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    $content=($lines -join "`r`n")+"`r`n"
    if (!(Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -ne $content) {
        [IO.File]::WriteAllText($path,$content,(New-Object Text.UTF8Encoding($false)))
    }
    return $cert
}

function Wait-GuiOptionalSigning([string]$Path,[switch]$NonInteractive) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "GUI executable is missing: $Path" }
    if ($NonInteractive) { return }
    $before=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $body=Get-BuildPayloadHash $Path
    Write-Host "GUI compiled: $Path"
    Write-Host 'Optional: sign this GUI file now. No signature is required. Final hashes are recorded after you continue.'
    $reply=Read-Host 'Press Enter to continue with the final GUI bytes; q cancels'
    if ($reply -eq 'q') { throw 'Build cancelled at the GUI signing step. GUI was preserved.' }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $before) { return }
    if ((Get-BuildPayloadHash $Path) -ne $body) { throw 'GUI executable content changed during signing. Rebuild before continuing.' }
    if ((Get-AuthenticodeSignature -LiteralPath $Path).Status -ne 'Valid') { throw 'Changed GUI must have a valid signature. Leave the compiled file unchanged to skip signing.' }
}

function Wait-Gen2OptionalSigning([switch]$NonInteractive) {
    $root=Get-BuildRoot
    $runtime=Join-Path $root 'build\CMP90HXUnlocker.exe'
    $before=Assert-Gen2Validated
    if ($NonInteractive) { return }
    $body=Get-BuildPayloadHash $runtime
    if (!$NonInteractive) {
        Write-Host "Gen2 compiled and tested: $runtime"
        Write-Host 'Optional: sign this file now. No signature is required.'
        $reply=Read-Host 'Press Enter to continue with the final Gen2 bytes; q cancels'
        if ($reply -eq 'q') { throw 'Build cancelled before caller policy generation. Gen2 was preserved.' }
    }
    $runtimeHash=(Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
    if ($runtimeHash -eq $before) { return }
    $candidate=$runtime
    if ((Get-BuildPayloadHash $candidate) -ne $body) { throw 'Gen2 executable content changed during signing. Rebuild and self-test first.' }
    if ((Get-AuthenticodeSignature -LiteralPath $candidate).Status -ne 'Valid') { throw 'Changed Gen2 must have a valid signature. Leave the compiled file unchanged to skip signing.' }
    & $candidate self-test | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Final Gen2 self-tests failed.' }
    $core=Join-Path $root 'vendor\nvpermissive-dist-469dc0c\obj\nvpermissive-core.o'
    if (Test-Path -LiteralPath $core) {
        & $candidate core-test --core $core | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Final Gen2 core tests failed.' }
    }
    $hash=(Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
    Set-Content -LiteralPath (Join-Path $root 'build\CMP90HXUnlocker.validated.sha256') -Value $hash -Encoding ASCII
}

function Read-BuildConfig {
    $path=Join-Path (Get-BuildRoot) '.build\local.json'
    if (!(Test-Path -LiteralPath $path)) { return [pscustomobject]@{Schema=1;CertificatePath='';TimestampUrl=''} }
    $value=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($value.Schema -ne 1) { throw "Unsupported local build configuration: $path" }
    return $value
}
function Save-BuildSigningConfig([string]$CertificatePath,[string]$TimestampUrl) {
    $directory=Join-Path (Get-BuildRoot) '.build'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    # Only paths/settings are saved. No password, certificate bytes or private key.
    $value=[ordered]@{Schema=1;CertificatePath=$CertificatePath;TimestampUrl=$TimestampUrl}
    $path=Join-Path $directory 'local.json'
    $temp=$path+'.tmp'
    $value | ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $path -Force
}
function Resolve-BuildMSBuild([string]$Explicit,[switch]$Driver) {
    if ($Explicit) {
        if (!(Test-Path -LiteralPath $Explicit -PathType Leaf)) { throw "MSBuild not found: $Explicit" }
        return [IO.Path]::GetFullPath($Explicit)
    }
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'VS Installer/vswhere is missing.' }
    $instances=@((& $vswhere -all -products '*' -requires Microsoft.Component.MSBuild -format json) | ConvertFrom-Json)
    foreach ($instance in ($instances | Sort-Object { [version]$_.installationVersion } -Descending)) {
        $base=$instance.installationPath
        $exe=Join-Path $base 'MSBuild\Current\Bin\MSBuild.exe'
        if (!(Test-Path -LiteralPath $exe)) { continue }
        if ($Driver) {
            $vc=Join-Path $base 'MSBuild\Microsoft\VC'
            $toolsets=@(Get-ChildItem -LiteralPath $vc -Filter Toolset.props -Recurse -ErrorAction SilentlyContinue |
                Where-Object FullName -like '*\Platforms\x64\PlatformToolsets\WindowsKernelModeDriver10.0\Toolset.props')
            if (!$toolsets.Count) { continue }
        }
        return $exe
    }
    if ($Driver) { throw 'No MSBuild installation with x64 WindowsKernelModeDriver10.0 found. Install WDK VS integration or pass -MSBuild.' }
    throw 'MSBuild is unavailable.'
}
function Resolve-BuildSignTool([string]$Explicit) {
    if ($Explicit) {
        if (!(Test-Path -LiteralPath $Explicit -PathType Leaf)) { throw "SignTool not found: $Explicit" }
        return [IO.Path]::GetFullPath($Explicit)
    }
    $kitRoot=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction Stop).KitsRoot10
    foreach ($version in (Get-ChildItem -LiteralPath (Join-Path $kitRoot 'bin') -Directory |
        Where-Object Name -match '^\d+\.\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending)) {
        $exe=Join-Path $version.FullName 'x64\signtool.exe'
        if (Test-Path -LiteralPath $exe) { return $exe }
    }
    throw 'Windows SDK x64 signtool.exe is unavailable.'
}
function Assert-BuildFramework([switch]$Gui) {
    $directory=Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8.1'
    $name=if($Gui) {'PresentationFramework.dll'} else {'mscorlib.dll'}
    if (!(Test-Path -LiteralPath (Join-Path $directory $name))) { throw '.NET Framework 4.8.1 Developer Pack is required.' }
}
function Get-Gen2ProjectOutput {
    $root=Get-BuildRoot
    [xml]$project=Get-Content -LiteralPath (Join-Path $root 'src\CMP90HXUnlockProgram.csproj') -Raw
    $names=@($project.Project.PropertyGroup.AssemblyName | Where-Object { $_ })
    if ($names.Count -ne 1 -or $names[0] -match '[\\/:$]') { throw 'Expected one literal AssemblyName in the Gen2 project.' }
    return Join-Path $root ('build\'+$names[0]+'.exe')
}
function Assert-Gen2Validated {
    $root=Get-BuildRoot
    $exe=Join-Path $root 'build\CMP90HXUnlocker.exe'
    $stamp=Join-Path $root 'build\CMP90HXUnlocker.validated.sha256'
    if (!(Test-Path -LiteralPath $exe) -or !(Test-Path -LiteralPath $stamp)) { throw 'Validated Gen2 is missing. Run build.ps1 or build-gen2.ps1 first.' }
    $hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    if ((Get-Content -LiteralPath $stamp -Raw).Trim() -ne $hash) { throw 'Gen2 validation stamp mismatch. Rebuild and run self-tests.' }
    return $hash
}
function Get-CallerPolicyContent([string]$Hash) {
    if ($Hash -cnotmatch '^[0-9A-F]{64}$') { throw 'Caller policy requires an uppercase SHA256 digest.' }
    $bytes=@(0..31 | ForEach-Object { '0x'+$Hash.Substring($_*2,2) }) -join ','
    return "/* Generated from the self-tested Gen2. Do not edit. */`r`n#define CMP_CALLER_SHA256_HEX `"$Hash`"`r`n#define CMP_CALLER_SHA256_BYTES {$bytes}`r`n"
}
function Write-CallerPolicy {
    $hash=Assert-Gen2Validated
    $content=Get-CallerPolicyContent $hash
    $path=Join-Path (Get-BuildRoot) 'build\caller-policy.h'
    if (!(Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -ne $content) {
        [IO.File]::WriteAllText($path,$content,[Text.Encoding]::ASCII)
    }
    return $hash
}
function Assert-DriverCallerPolicy([string]$Path,[string]$WorkerHash) {
    # Read the policy from the actual signed binary, never trust a sidecar stamp.
    $content=[Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($Path))
    $matches=[regex]::Matches($content,'CMP90HX_CALLER_SHA256=([0-9A-F]{64})\x00')
    if ($matches.Count -ne 1 -or $matches[0].Groups[1].Value -cne $WorkerHash) {
        throw 'Driver caller policy does not match Gen2. Rebuild the driver with build-dma-driver.ps1, sign that new driver, and select it with -DriverPath.'
    }
}
function Select-BuildCertificate([string]$Explicit,[switch]$Choose,[switch]$NonInteractive) {
    if ($Explicit) { return [IO.Path]::GetFullPath($Explicit) }
    $config=Read-BuildConfig
    if (!$Choose -and $config.CertificatePath -and (Test-Path -LiteralPath $config.CertificatePath -PathType Leaf)) { return $config.CertificatePath }
    if ($NonInteractive) { throw 'No saved signing certificate. Pass -CertificatePath (PFX/P12/ZIP) or run interactively.' }
    if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
        $selected=Read-Host 'Signing certificate path (PFX/P12/ZIP; use powershell.exe -STA for the file picker)'
        if (!$selected.Trim()) { throw 'Certificate selection cancelled; compiled Gen2 was preserved.' }
        return [IO.Path]::GetFullPath($selected.Trim().Trim('"'))
    }
    Add-Type -AssemblyName System.Windows.Forms
    $dialog=New-Object System.Windows.Forms.OpenFileDialog
    try {
        $dialog.Title='Select the Gen2 signing certificate (PFX/P12 or ZIP containing one)'
        $dialog.Filter='Signing certificates (*.pfx;*.p12;*.zip)|*.pfx;*.p12;*.zip'
        $dialog.CheckFileExists=$true
        $suggested=$config.CertificatePath
        if (!$suggested) { $suggested=Join-Path (Split-Path -Parent (Get-BuildRoot)) '5cca7e4891a7ed8f653b3720ece7a6f7.zip' }
        if (Test-Path -LiteralPath $suggested) { $dialog.InitialDirectory=Split-Path -Parent $suggested; $dialog.FileName=Split-Path -Leaf $suggested }
        if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw 'Certificate selection cancelled; compiled Gen2 was preserved.' }
        return $dialog.FileName
    } finally { $dialog.Dispose() }
}
function Protect-BuildCertificatePath([string]$Path) {
    $root=[IO.Path]::GetFullPath((Get-BuildRoot)).TrimEnd('\')
    if (!$Path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { return }
    $relative=$Path.Substring($root.Length+1).Replace('\','/')
    $tracked=@(& git -C $root ls-files -- $relative)
    if ($LASTEXITCODE -ne 0) { throw 'Could not check whether the signing bundle is tracked by Git.' }
    if ($tracked.Count) { throw 'The selected signing bundle is tracked by Git. Remove it from the index before signing.' }
    & git -C $root check-ignore --quiet -- $relative
    if ($LASTEXITCODE -eq 0) { return }
    # A per-checkout exclude protects any user-selected ZIP without publishing its name.
    $exclude=(& git -C $root rev-parse --git-path info/exclude).Trim()
    if (![IO.Path]::IsPathRooted($exclude)) { $exclude=Join-Path $root $exclude }
    $escaped=$relative.Replace('[','\[').Replace(']','\]').Replace('*','\*').Replace('?','\?')
    Add-Content -LiteralPath $exclude -Value ('/'+$escaped) -Encoding UTF8
    & git -C $root check-ignore --quiet -- $relative
    if ($LASTEXITCODE -ne 0) { throw 'Could not exclude the signing bundle from Git.' }
}
function Read-BuildBundlePassword([string]$Path) {
    if ([IO.Path]::GetExtension($Path) -ine '.zip') { return $null }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries=@($zip.Entries | Where-Object Name -EQ 'certificate.txt')
        if ($entries.Count -ne 1 -or $entries[0].Length -gt 4096) { return $null }
        $reader=New-Object IO.StreamReader($entries[0].Open())
        try { $text=$reader.ReadToEnd() } finally { $reader.Dispose() }
        $match=[regex]::Match($text,'(?im)^[^\r\n]*password[^\r\n:]*:\s*([^\r\n]+)\s*$')
        if ($match.Success) { return ConvertTo-SecureString $match.Groups[1].Value.Trim() -AsPlainText -Force }
    } finally { $text=$null; $zip.Dispose() }
    return $null
}
function Read-BuildPfx([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Signing certificate not found: $Path" }
    if ([IO.Path]::GetExtension($Path) -ieq '.zip') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip=[IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $entries=@($zip.Entries | Where-Object { $_.Name -match '\.(pfx|p12)$' })
            if ($entries.Count -ne 1 -or $entries[0].Length -gt 10MB) { throw 'Signing ZIP must contain exactly one PFX/P12 (at most 10 MiB).' }
            $stream=$entries[0].Open(); $memory=New-Object IO.MemoryStream
            try { $stream.CopyTo($memory); return ,$memory.ToArray() }
            finally { $stream.Dispose(); $memory.Dispose() }
        } finally { $zip.Dispose() }
    }
    if ([IO.Path]::GetExtension($Path) -notin @('.pfx','.p12')) { throw 'Select a PFX/P12 or ZIP containing a PFX/P12.' }
    return ,[IO.File]::ReadAllBytes($Path)
}
function Import-BuildPfx([byte[]]$Bytes,[Security.SecureString]$Password,[Security.Cryptography.X509Certificates.X509KeyStorageFlags]$Flags) {
    $pointer=[IntPtr]::Zero
    try {
        $plain=''
        if ($null -ne $Password) { $pointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password); $plain=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
        $collection=New-Object Security.Cryptography.X509Certificates.X509Certificate2Collection
        $collection.Import($Bytes,$plain,$Flags)
        return ,$collection
    } finally {
        $plain=$null
        if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    }
}
function Invoke-BuildOpenSsl([string]$Executable,[string[]]$Arguments,[string]$InputText) {
    $quoted=@($Arguments | ForEach-Object { '"'+[regex]::Replace([regex]::Replace($_,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"' })
    $process=New-Object Diagnostics.Process
    $process.StartInfo=New-Object Diagnostics.ProcessStartInfo
    $process.StartInfo.FileName=$Executable
    $process.StartInfo.Arguments=$quoted -join ' '
    $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardInput=$true; $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
    $inputBytes=(New-Object Text.UTF8Encoding($false)).GetBytes($InputText)
    try {
        if (!$process.Start()) { throw 'OpenSSL could not start.' }
        $output=$process.StandardOutput.ReadToEndAsync(); $errors=$process.StandardError.ReadToEndAsync()
        # Exact UTF-8/LF input: an MSYS OpenSSL treats the CR in CRLF as password data.
        $process.StandardInput.BaseStream.Write($inputBytes,0,$inputBytes.Length)
        $process.StandardInput.Close(); $process.WaitForExit()
        return [pscustomobject]@{ExitCode=$process.ExitCode;Output=$output.Result;Error=$errors.Result}
    } finally { [Array]::Clear($inputBytes,0,$inputBytes.Length); $process.Dispose() }
}
function Import-BuildSigningPfx([byte[]]$Bytes,[Security.SecureString]$Password,[Security.Cryptography.X509Certificates.X509KeyStorageFlags]$Flags) {
    try { return ,(Import-BuildPfx $Bytes $Password $Flags) }
    catch {
        # Some PBES2/MAC encodings are readable by OpenSSL but rejected by Windows.
        # Normalize the PFX container only; the certificate and key stay identical.
        $command=Get-Command openssl -ErrorAction SilentlyContinue
        $openssl=if($command) {$command.Source} else {''}
        if (!$openssl) {
            $git=Get-Command git -ErrorAction SilentlyContinue
            if ($git) { $openssl=Join-Path (Split-Path -Parent (Split-Path -Parent $git.Source)) 'usr\bin\openssl.exe' }
        }
        if (!$openssl -or !(Test-Path -LiteralPath $openssl)) { throw 'Windows cannot import this PFX. Use a Windows-compatible PFX or install OpenSSL (Git for Windows includes it).' }
    }
    $directory=Join-Path (Get-BuildRoot) ('.build\signing-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
    }
    Set-Acl -LiteralPath $directory -AclObject $acl
    $inputPath=Join-Path $directory 'input.pfx'; $outputPath=Join-Path $directory 'normalized.pfx'
    $pointer=[IntPtr]::Zero; $normalized=$null
    try {
        [IO.File]::WriteAllBytes($inputPath,$Bytes)
        $plain=''
        if ($null -ne $Password) { $pointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password); $plain=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
        $decoded=Invoke-BuildOpenSsl $openssl @('pkcs12','-in',$inputPath,'-nodes','-passin','stdin') ($plain+"`n")
        if ($decoded.ExitCode -ne 0) { throw 'PFX could not be opened. Check the certificate password.' }
        $publicPem=(@([regex]::Matches($decoded.Output,'(?s)-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----') | ForEach-Object Value) -join "`n")+"`n"
        $pem=(@([regex]::Matches($decoded.Output,'(?s)-----BEGIN PRIVATE KEY-----.*?-----END PRIVATE KEY-----') | ForEach-Object Value) -join "`n")+"`n"
        $decoded=$null
        # Temporary key/PFX files inherit the session's current-user/SYSTEM-only ACL.
        $publicPath=Join-Path $directory 'public.pem'
        $privatePath=Join-Path $directory 'private.pem'
        [IO.File]::WriteAllText($publicPath,$publicPem,(New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText($privatePath,$pem,(New-Object Text.UTF8Encoding($false)))
        $pem=$null
        $encoded=Invoke-BuildOpenSsl $openssl @('pkcs12','-export','-in',$publicPath,'-inkey',$privatePath,'-out',$outputPath,'-passout','pass:','-keypbe','PBE-SHA1-3DES','-certpbe','PBE-SHA1-3DES','-macalg','sha1') ''
        if ($encoded.ExitCode -ne 0) { throw ('PFX compatibility conversion failed: '+$encoded.Error.Trim()) }
        $normalized=[IO.File]::ReadAllBytes($outputPath)
        return ,(Import-BuildPfx $normalized $null $Flags)
    } finally {
        $plain=$null; $pem=$null
        if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
        if ($normalized) { [Array]::Clear($normalized,0,$normalized.Length) }
        # Delete only known files within this exact freshly created session directory.
        foreach ($name in @('input.pfx','normalized.pfx','public.pem','private.pem','openssl.log')) {
            $path=Join-Path $directory $name
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
        Remove-Item -LiteralPath $directory
    }
}
function Get-BuildPayloadHash([string]$Path) {
    # Compare executable bytes while excluding only Authenticode's checksum/table.
    $bytes=[IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes,0) -ne 0x5a4d) { throw 'Invalid PE image.' }
    $pe=[BitConverter]::ToInt32($bytes,60)
    if ($pe -lt 64 -or $pe -gt $bytes.Length-256 -or [BitConverter]::ToUInt32($bytes,$pe) -ne 0x4550) { throw 'Invalid PE header.' }
    $optional=$pe+24
    $magic=[BitConverter]::ToUInt16($bytes,$optional)
    if ($magic -eq 0x20b) { $directories=$optional+112 }
    elseif ($magic -eq 0x10b) { $directories=$optional+96 }
    else { throw 'Unsupported PE optional header.' }
    $security=$directories+32
    $certificate=[BitConverter]::ToUInt32($bytes,$security)
    $size=[BitConverter]::ToUInt32($bytes,$security+4)
    $length=$bytes.Length
    if ($certificate -or $size) {
        if (!$certificate -or !$size -or ($certificate % 8) -ne 0 -or
            [uint64]$certificate+$size -ne $bytes.Length -or $certificate -le $security+8) { throw 'Unexpected PE certificate layout.' }
        $length=[int]$certificate
    }
    # SignTool may add up to seven zero bytes to align the certificate table.
    $aligned=[int]([Math]::Ceiling($length/8.0)*8)
    $payload=New-Object byte[] $aligned
    [Array]::Copy($bytes,$payload,$length)
    [Array]::Clear($payload,$optional+64,4)
    [Array]::Clear($payload,$security,8)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($payload)).Replace('-','') }
    finally { $sha.Dispose() }
}
function Wait-BuildSignedDriver([string]$UnsignedPayloadHash,[string]$Path,[switch]$NonInteractive) {
    $root=Get-BuildRoot
    if (!$Path) { $Path=Join-Path $root 'build\dma-driver\CMP90HXDma.sys' }
    Write-Host 'Driver build finished. Sign build\dma-driver\CMP90HXDma.sys externally.'
    Write-Host 'Sign CMP90HXDma.sys in place; keep its file name. A custom .sys path can also be entered below.'
    while ($true) {
        try {
            $Path=[IO.Path]::GetFullPath($Path)
            if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Signed driver not found yet.' }
            $signature=Get-AuthenticodeSignature -LiteralPath $Path
            if ($signature.Status -ne 'Valid') { throw "Driver signature is not valid: $($signature.Status)" }
            if ((Get-BuildPayloadHash $Path) -ne $UnsignedPayloadHash) { throw 'Signed driver is from a different build. Sign the newly compiled driver.' }
            return $Path
        } catch {
            if ($NonInteractive) { throw }
            Write-Host $_.Exception.Message -ForegroundColor Yellow
            $reply=Read-Host 'Finish signing, then Enter to retry; or enter signed .sys path (q cancels)'
            if ($reply -eq 'q') { throw 'Release cancelled at the driver signing step. Build outputs are preserved.' }
            if ($reply.Trim()) { $Path=$reply.Trim().Trim('"') }
        }
    }
}
