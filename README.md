# 90HXWinUnlockConsole

独立 WPF 控制台项目，保留界面、状态读取、设备/服务管理、计划任务、功耗控制及部署代码。
执行 `./build.ps1` 只编译 GUI，输出 `build/CMP90HXControl.exe`，不构建或加载任何解锁驱动。
需要 VS MSBuild、.NET Framework 4.8.1 Developer Pack；`cert/` 仅为现有公共证书。

源码开发运行时，现有 Native 适配器从同级 `../90HXWinNativeUnlock` 查找 worker、驱动和核心。
已有完整发布包的 `runtime/driver/core` 布局不变。签名后使用本目录 `update-release-hashes.ps1` 更新配套哈希。
重构 Native GUI 包装入口为 `tools/build-rebuilt-gui.ps1`，读取 Native 的构建产物。

本次只整理项目边界：GUI 当前保留 Native 适配器；Session 方案的界面选择/调度尚未接入。
两种解锁方案各自拥有驱动、核心与命令行工具，GUI 后续通过明确的后端适配器调用。
不合并两个同名 worker，不移除驱动调用者哈希与签名检查。
迁移清单在 `migration/source-copy-manifest.json`；原项目暂保留作备份。
原Native发布入口也已迁移：build-app.ps1、build-release.ps1、update-package-hashes.ps1。它们从同级Native读取worker/驱动/核心，GUI和发布输出保留在Console；未执行签名或发布流程。
