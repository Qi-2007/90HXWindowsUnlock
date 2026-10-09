# 90HXWinUnlockConsole

Console 1.3.0 包含两个常驻角色：

- `build/CMP90HXControl.exe`：WPF 前台，仅通过共享内存发送指令、显示状态与日志。
- `build/CMP90HXBackground.exe`：后台硬件控制器，按配置选择 Native 或 Session，统一执行读取、解锁、功耗监控和省电。

两个角色各自使用机器范围的单实例互斥锁。打开前台会先连接已有后台，没有后台则隐藏启动。关闭前台不会取消已提交的硬件操作，也不会结束省电。后台需要管理员或 SYSTEM 权限；前台保留管理员启动方式，以连接受保护的共享内存。

## 构建和运行

安装 VS MSBuild 与 .NET Framework 4.8.1 Developer Pack，然后运行：

```powershell
.\build.ps1
.\build\CMP90HXControl.exe
```

Native 和 Session 先分别编译、签名，将完整配套成品放入本项目的 backends/native 与 backends/session。Console 编译时校验这些输入，生成哈希和独立版本信息，仅编译前后台，不再查找或编译同级后端源码项目。输入文件清单见 [独立后端包](docs/backend-packages.md)。

在前台右上角“设置”选择方案及自动操作。配置保存在 `C:\ProgramData\CMP90HX\settings.json`，具体字段见 [后台说明](docs/background-control.md)。已有 `Power` 和 `DisableAutoUnlock` 设置保留。

## Session 方案

解锁仍然走已验证的临时功能驱动流程：停用 NVIDIA、临时绑定、独立 IOCTL 解锁、确认 DMA 释放，再恢复原 NVIDIA INF。

正式方案不依赖 Lower Filter。后台仅在解锁时调用临时 UnlockSession 功能驱动，完成后恢复 NVIDIA；部署只复制 UnlockSession 包和配套 worker。

Native 和 Session 的当前解锁状态统一通过独立 CMP90HXReader 只读驱动采集。它不使用 DMA、不写寄存器、不挂入显卡设备栈。源码、签名和实机验证步骤见 [共用读取驱动](docs/shared-reader.md)。读取失败时不复用上次解锁结果。

## 验证范围

前后台编译及真实共享内存通信已通过，包括 8 个并发请求、后台单实例、前台客户端连接和正常退出。诊断模式不会访问显卡、修改配置或安装驱动。复查前台程序集确认没有 PnP、NVAPI、解锁工作流实现。

前后台拆分未改动已验证的 Session 解锁核心。实际解锁、功耗与睡眠唤醒仍需配套签名驱动实机验证。

所有驱动均通过驱动管理手动安装。环境检查只查询内核 DMA 保护，开启时禁止 Native。切换模式会卸载旧模式驱动；唤醒通知注册失败只报错。详见后台说明。
