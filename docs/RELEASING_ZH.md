# Console 发布步骤

## 首次引导与文档链接

默认地址常量位于 `ui/OnboardingPolicy.cs`：`https://90hx.445533.xyz/onboarding/officialsetup.html`。首次打开前台时通过系统默认浏览器打开引导，主窗口“设置”右侧的“文档”按钮随时打开同一地址。

交互运行 `build-release.ps1` 时会提示输入引导链接，直接按 Enter 使用默认地址。也可传入 `-OnboardingUrl 'https://你的域名/另一个引导.html'`；非交互模式省略参数时使用默认值。`build.ps1` 和 `build-gui.ps1` 同样支持此参数。链接编译为前台内的常量，不增加发布包配置文件，也不修改源码中的默认值。

`-PackageOnly` 的链接必须与上次编译一致，不能只打包就更改已编译的链接。编译记录中的 `OnboardingUrl` 可用于核对。

首次打开记录保存在 `%ProgramData%\CMP90HX\Onboarding`，按引导 URL 区分。同一台电脑相同链接后续启动不重复打开，换用另一发布版本的链接时会打开对应引导。预览、截图及后台单独启动不会触发；浏览器启动失败时保留重试机会，并写入前台日志。

前后台日志、配置、运行副本及引导记录统一保存在 `%ProgramData%\CMP90HX`。前台正常启动时会迁移当前用户旧的 `%LOCALAPPDATA%\CMP90HX` 内容；占用中的旧文件保留，后续启动重试迁移。日常启动保留 `ErrorLogs` 错误归档，成功保存后总数超过 10 份才删除最旧归档。一键卸载成功后，退出前后台并清理新旧数据目录，包含所有错误归档；仍被占用的文件和目录安排在 Windows 重启后删除。

默认构建不包含根证书 UI、安装/卸载功能、证书安装前置检查或证书文件。默认编译使用 `build.ps1`；需要根证书功能时使用 `build.ps1 -EnableRootCertificates`，可另带 `-CertificateDirectory` 选择公开证书目录。直接 MSBuild 使用 `/p:RootCertificateSupport=true`。

正式发布同样默认关闭根证书功能：`build-release.ps1`。启用时使用 `build-release.ps1 -EnableRootCertificates`；`-PackageOnly` 必须与上次编译的选项一致。驱动签名验证不受此开关影响，默认版的一键卸载也不会操作系统根证书。

启用根证书功能时，手动安装读取驱动、Native 或 Session 解锁驱动会先检查系统根证书；缺失时自动校验并安装随包的公开证书，已安装时直接继续。证书安装或安装后验证失败会中止驱动安装。只读状态查询和解锁操作不自动导入证书。

在 Console 项目目录操作。Native/Session 的签名配套包放在 `backends/native` 与 `backends/session`，Console 构建自动计算哈希、检查 SYS/worker 配对并编译前后台，不会重编后端。

Reader 位于本项目 `reader`，独立版本为 1.0.1.0，EXE/SYS 使用统一产品名 `CMP 90HX Windows Unlock`。修改客户端后须重建 SYS；签名客户端必须在生成驱动调用者哈希之前完成。

`build.ps1`、`build-gui.ps1` 和发布时的编译流程都会检查 `build/reader`。缺少 SYS 或客户端时自动编译；已有时询问是否重新编译，默认保留。重编后等待用户原地签名 `build/reader/CMP90HXReader.sys`，验证签名及编译内容后自动更新最终哈希，再编译 Console。现有 SYS 未签名时也会等待签名。取消会保留编译产物。

自动化可使用 `-NonInteractive`：已有 Reader 时直接复用；缺失时编译，但未签名会明确报错退出。签名后重新运行即可。`-RebuildReader` 可强制重编；不能与 `-PackageOnly` 一起使用。

```powershell
# 关闭 Console 前台。如果本项目的后台正在运行，在管理员终端正常退出它。
.\build\CMP90HXBackground.exe --request Shutdown
# 按提示选择是否重编 Reader；编译后签 SYS，按 Enter 继续。
.\build-release.ps1
# 或强制重编 Reader。
.\build.ps1 -RebuildReader
```

发布输出为 `dist/CMP90HX-Control-时间戳/` 及同名 ZIP。使用白名单，仅包含 Console、后台、Native/Session/Reader 运行文件、`files.sha256.json`、中英文 README 和 LICENSE；启用根证书功能时额外包含证书。`backend-inputs.json`、`compiled-backends.json`、`release.json`、`BACKEND_PACKAGES.md`、`THIRD_PARTY_NOTICES.md`、调试符号和编译缓存不进入发布包。构建记录保留在本地 build 目录。正式发布要求 Native SYS、Session SYS/CAT、Reader SYS 签名有效。

如果已经编译完成，且所有文件及签名都没有变化，可使用 `build-release.ps1 -PackageOnly`。修改签名会改变文件哈希，需要更新 Reader pins（若改动 Reader）并重新编译后再打包。

`clean-build.ps1 -WhatIf` 预览整理，`clean-build.ps1` 执行。保留当前运行文件、配套输入与哈希策略；旧包、备份和预览归档到 `.build/archive`，可重建的旧编译缓存删除，正在使用的文件跳过。新编译缓存位于 `.build/obj`，不会堆积在 build 根目录。
