# 自签名测试驱动

以下操作在自己的 Windows 测试机上执行。私钥保留在签名机器，公开 `.cer` 可以
导入测试机。本仓库不提交证书或私钥。生产环境正常签名策略需采用微软认可的驱动签名。

## 生成和信任证书

管理员 PowerShell：

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject 'CN=CMP90HX DMA Test' -CertStoreLocation 'Cert:\LocalMachine\My' `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(2)
$cerPath = Join-Path $PWD 'CMP90HX-Dma-Test.cer'
Export-Certificate -Cert $cert -FilePath $cerPath
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\Root'
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher'
$cert.Thumbprint
```

如果在另一台机器加载驱动，只复制公开证书并执行两个 Import-Certificate。
不要导出或传输签名私钥。

## 签名已编译的驱动

把 SDK 版本和证书指纹替换为实际值：

```powershell
$signTool = 'C:\Program Files (x86)\Windows Kits\10\bin\<SDK版本>\x64\signtool.exe'
$thumbprint = '<证书指纹>'
& $signTool sign /v /fd SHA256 /sm /s My /sha1 $thumbprint .\build\dma-driver\CMP90HXDma.sys
if ($LASTEXITCODE -ne 0) { throw 'Signing failed' }
Get-AuthenticodeSignature .\build\dma-driver\CMP90HXDma.sys
```

每次重新编译后重新签名。`/sha1` 用于按证书指纹选择证书，文件签名摘要为 SHA256。

## 测试模式

管理员终端：

```powershell
bcdedit /set testsigning on
```

重启后生效。如果设置被 Secure Boot 策略拒绝，这条自签名测试路径需要在 BIOS/UEFI
关闭 Secure Boot。导入证书不能替代测试模式。脚本不会自动更改这些设置。
Authenticode 为 Valid 仍不保证内核接受驱动；加载失败要检查系统事件和实际错误码。

完成后按 README 运行安装脚本和 CPU 映射测试。当前驱动没有卸载入口；
退出测试需完全关机重启，让按需服务保持未启动，然后才能清理服务或更新驱动。
取消测试模式可执行 `bcdedit /set testsigning off` 并重启。
