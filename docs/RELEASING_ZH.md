# Console 发布步骤

默认构建不包含根证书 UI、安装/卸载功能、证书安装前置检查或证书文件。默认编译使用 `build.ps1`；需要根证书功能时使用 `build.ps1 -EnableRootCertificates`，可另带 `-CertificateDirectory` 选择公开证书目录。直接 MSBuild 使用 `/p:RootCertificateSupport=true`。

正式发布同样默认关闭根证书功能：`build-release.ps1 -NonInteractive`。启用时使用 `build-release.ps1 -NonInteractive -EnableRootCertificates`；`-PackageOnly` 必须与上次编译的选项一致。驱动签名验证不受此开关影响，默认版的一键卸载也不会操作系统根证书。

在 Console 项目目录操作。Native/Session 的签名配套包放在 `backends/native` 与 `backends/session`，Console 构建自动计算哈希、检查 SYS/worker 配对并编译前后台，不会重编后端。

Reader 位于本项目 `reader`，独立版本为 1.0.1.0，EXE/SYS 使用统一产品名 `CMP 90HX Windows Unlock`。修改客户端后须重建 SYS；签名客户端必须在生成驱动调用者哈希之前完成。

```powershell
# 需要修改/重新编译 Reader 时才执行；之后用自己的流程签 SYS。
.\build-reader-driver.ps1
# 签 build\reader\CMP90HXReader.sys 后，更新最终文件哈希。
.\build-reader-driver.ps1 -UpdatePinsOnly
# 关闭 Console 前台。如果本项目的后台正在运行，在管理员终端正常退出它。
.\build\CMP90HXBackground.exe --request Shutdown
# 自动编译前后台，检查签名和配套文件，生成 dist 目录及 ZIP。
.\build-release.ps1 -NonInteractive
```

发布输出为 `dist/CMP90HX-Control-时间戳/` 及同名 ZIP。包含 Console、后台、Native/Session/Reader 配套文件、许可和哈希清单；启用根证书功能时额外包含证书。正式发布要求 Native SYS、Session SYS/CAT、Reader SYS 签名有效。

如果已经编译完成，且所有文件及签名都没有变化，可使用 `build-release.ps1 -PackageOnly`。修改签名会改变文件哈希，需要更新 Reader pins（若改动 Reader）并重新编译后再打包。

`clean-build.ps1 -WhatIf` 预览整理，`clean-build.ps1` 执行。保留当前运行文件、配套输入与哈希策略；旧包、备份和预览归档到 `.build/archive`，可重建的旧编译缓存删除，正在使用的文件跳过。新编译缓存位于 `.build/obj`，不会堆积在 build 根目录。
