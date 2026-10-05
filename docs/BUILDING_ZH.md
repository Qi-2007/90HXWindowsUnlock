# 编译、签名和发布

在仓库根目录的 PowerShell 中执行。需要 .NET Framework 4.8.1 Developer Pack、Visual Studio MSBuild；编译驱动还需要匹配的 SDK/WDK 及 VS 的 x64 内核工具集集成。脚本按安装的工具集选择 VS，不再固定要求 MSBuild 17。

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
```

## 独立操作

| 命令 | 功能 |
| --- | --- |
| `.\build-dma-driver.ps1` | 构建/自测 Gen2、自动生成调用白名单并编译驱动，输出未签名的 `build\dma-driver\CMP90HXDma.sys` |
| `.\build-gen2.ps1` | 编译 Gen2、运行模拟自测、选择证书、签名、验证签名及再次自测 |
| `.\build-gui.ps1` | 编译 GUI，使用当前源码里的运行文件哈希；完成后暂停供可选外部签名，回车可跳过 |
| `.\build.ps1` | 只编译 Gen2 并运行模拟自测，不签名；供开发和现有工具使用 |
| `.\sign-gen2.ps1` | 给已经通过自测的 Gen2 签名，不重复编译 |

编译脚本支持 `-Clean`，不删除整个 `build`，保留其他项目产物、证书和已签名驱动。驱动脚本额外按需构建/自测 Gen2；Visual Studio 直接构建驱动前先运行该脚本生成白名单。

当前项目文件为 `src\CMP90HXUnlockProgram.csproj`、`ui\CMP90HXUnlockConsole.csproj` 和 `driver\CMP90HXDma.vcxproj`。工作进程统一使用 `CMP90HXUnlocker.exe`，编译、签名、GUI 调用和打包都使用这一个文件及其配置文件，不再生成兼容副本。

## Gen2 签名证书（可选）

完整发布不要求或自动执行 Gen2 签名；Gen2 构建/自测后会暂停，允许外部签名，直接回车则跳过。调用授权由驱动内置的最终 EXE SHA-256 决定。
以下独立签名工具仍保留。如果主动签名，必须在生成驱动白名单之前签名，
之后运行 `build-dma-driver.ps1`；任何 EXE 字节变化都会使原驱动拒绝它。

首次执行 `build-gen2.ps1`，编译完成后选择 `.pfx`、`.p12`，或包含一个 PFX/P12 的 `.zip`。Windows PowerShell 的 STA 会弹出文件选择框；MTA 终端使用路径输入。也可以显式指定：

```powershell
.\build-gen2.ps1 -CertificatePath 'D:\Signing\gen2.zip'
```

选择的路径和 RFC3161 时间戳服务器保存在 `.build\local.json`，下次自动读取。配置只保存路径与服务器地址，不保存密码、私钥或证书内容。强制重新选择：

```powershell
.\build-gen2.ps1 -ChooseCertificate
```

带密码的 PFX 使用安全输入；ZIP 内若有 `certificate.txt`，包含 `Password: ...` 的单行说明，可以直接在内存中读取，不复制到配置。如果没有该说明，可在运行时输入密码，或者传入 SecureString：

```powershell
$password = Read-Host 'PFX 密码' -AsSecureString
.\build-gen2.ps1 -CertificatePath 'D:\Signing\gen2.pfx' -CertificatePassword $password
```

证书必须具有普通 EXE 的 Code Signing 用途（OID `1.3.6.1.5.5.7.3.3`），且证书链在本机受信任。只有内核驱动签名用途的证书不适用于 Gen2。脚本不会自动修改系统根证书信任库。

Windows 无法导入某些 PFX 容器编码时，脚本会尝试 OpenSSL 兼容转换；支持 PATH 中的 OpenSSL 或 Git for Windows 附带的 OpenSSL。转换不修改原始包，临时材料位于仅当前用户和 SYSTEM 可访问的 `.build` 子目录，正常成功或异常退出都会清理。转换只改变 PFX 容器，不改变证书和私钥。

签名使用 SHA-256 和 RFC3161 时间戳，密码不出现在 SignTool 命令行。新增的 CurrentUser/My 签名证书与私钥在操作结束后移除；原先已有的证书保留。签名失败时保留原始编译文件，不更新其验证记录。

`.build`、PFX/P12、私钥文件及指定的证书压缩包已被 Git 忽略；选择仓库内其他 ZIP 时会加入本地 `.git/info/exclude`。已被 Git 跟踪的签名包会被拒绝使用，应先将它移出索引。发布使用明确的文件清单，不包含本地签名材料或编译配置。

## 编译 Gen2 → 核对白名单 → 编译 GUI

```powershell
.\build-app.ps1
```

该流程复用已有的 `build\dma-driver\CMP90HXDma.sys`。也可用 `-DriverPath` 选择已有签名驱动。先编译并自测 Gen2，暂停供可选签名，再校验驱动签名及其内置的调用白名单、更新 GUI 中的 Gen2/驱动哈希，最后编译 GUI。不要求 Gen2 签名。

Gen2 改变后须执行完整发布，重新构建并签名匹配的驱动。仅修改 GUI 可复用已有匹配驱动；单独 `build-gui.ps1` 不自动构建 Gen2 或修改哈希。

## 完整发布

```powershell
.\build-release.ps1
```

发布验证无需为了读取已有计划任务而提权；无读取权限时会报告明确状态，继续执行模拟测试和打包。实际安装/卸载证书、驱动及计划任务仍由运行程序请求管理员权限。

顺序如下：

1. 重建 Gen2，运行模拟自测。
2. 暂停等待可选 Gen2 签名：给 `build/CMP90HXUnlocker.exe` 原地签名后回车；也可不签名直接回车。签名必须保持可执行内容不变且验证有效；改变的最终文件会重新自测并更新验证记录，然后自动生成 `build/caller-policy.h`。`-NonInteractive` 自动跳过这一暂停。
3. 重建 DMA 驱动，嵌入调用白名单，记录本次未签名 PE 的内容摘要。
4. 等待你在外部给 `build/dma-driver/CMP90HXDma.sys` 原地签名，保持原文件名不变；也可在提示中输入其他完整路径。按 Enter 重新检查，输入 `q` 取消。
5. 检查驱动签名有效，并确认其可执行内容与本次构建相同，拒绝误用旧签名文件。比较仅排除 Authenticode 校验和、证书表和必要的对齐填充。
6. 从实际驱动二进制核对内置白名单与 Gen2 一致，自动更新运行文件哈希，编译 GUI。此时暂停，可给 `build/CMP90HXControl.exe` 签名后回车，也可不签名直接回车。继续后记录最终 GUI 哈希，运行工作流模拟测试、包验证和无窗口启动验证，再生成包含最终文件哈希的清单。签名不得改变 GUI 的可执行内容。
7. 创建 `dist` 目录中的 ZIP、文件哈希清单及 ZIP 的 SHA-256 文件。

仅驱动签名由你完成。脚本不安装驱动，不禁用显卡，不执行硬件解锁。

已准备好自测通过的 Gen2 和匹配的签名驱动，只需要重新构建 GUI、验证和打包时：

```powershell
.\build-release.ps1 -PackageOnly
```

此模式复用已准备的产物，不执行驱动重建及人工签名等待。Gen2 的自测记录必须与文件哈希一致，驱动内置白名单也必须匹配。旧版本没有白名单的驱动会被拒绝。

驱动启动后再启动 Gen2。驱动通过进程创建通知保存实际 EXE 文件对象，打开设备时在 PASSIVE_LEVEL 完整读取并计算 SHA-256；不是按程序名称、路径或调用者提交的哈希放行。独立探测程序不在白名单中。已加载驱动不能热更新，换版本需重启。

白名单只覆盖磁盘 EXE，不涵盖 DLL、配置文件和运行时内存；它不能防御已取得管理员或内核权限的攻击者。实机加载/打开设备验证仍待完成；新增 `/INTEGRITYCHECK` 和进程通知要求驱动签名，旧的未签名加载冒烟流程不再适用。

## 常用参数

GUI 编译和完整发布默认读取仓库根目录的 `cert/`，必须有且只有一个公开 `.cer` 或 `.crt` 文件；支持 `-CertificateDirectory` 指定其他目录。构建生成 `build/CertificatePolicy.cs`，记录名称、指纹和 SHA-256；GUI 显示、检测、安装及卸载仅针对这张证书。发布将它复制到 `driver/cert/`，运行时校验指纹和文件哈希。改变证书后必须重新构建 GUI 和发布包，不再使用旧的两个固定证书。

- `-MSBuild`：C# 项目使用的 MSBuild 完整路径。
- `-DriverMSBuild`：完整发布时驱动使用的 MSBuild；独立驱动脚本使用 `-MSBuild`。
- `-SdkVersion`：指定匹配的 SDK/WDK，例如 `10.0.28000.0`。
- `-SignTool`：独立 Gen2 签名工具使用的 SignTool 完整路径。
- `-TimestampUrl`：独立 Gen2 签名工具使用的 RFC3161 时间戳服务器，默认 `http://timestamp.digicert.com`。
- `-OutputDirectory`：发布输出目录，必须尚不存在。
- `-NonInteractive`：禁止选择框、密码提示和人工等待；缺少资料或签名无效时直接失败。完整发布通常使用交互模式。

`build-gui.ps1`、`build-app.ps1`、`build-release.ps1`（含 `-PackageOnly`）以及会重建 GUI 的 `update-release-hashes.ps1` 都提供 GUI 签名暂停；使用 `-NonInteractive` 跳过。暂停时输入 `q` 可取消并保留已编译文件。

PowerShell 源脚本统一保存为带 BOM 的 UTF-8，兼容 Windows PowerShell 5.1 的中文读取。构建入口同时统一控制台输入、输出与 `$OutputEncoding` 为 UTF-8，避免 MSBuild/工作进程改变代码页后被错误解码。发布文件名固定为 `CMP90HXUnlocker.exe` 和 `CMP90HXDma.sys`；签名不会修改文件名。自定义驱动路径使用 `-DriverPath`。

`update-release-hashes.ps1` 保留旧入口；完整发布传入 `-SkipWorkerBuild`，避免改变已经编入驱动的 Gen2。`-RequireSignedWorker` 仍可选，但正常发布不使用。旧的程序签名参数在 `build-app.ps1` / `build-release.ps1` 中仅为调用兼容保留，不再执行签名。
