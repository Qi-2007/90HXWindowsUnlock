# 前后台与驱动管理

前台通过共享内存向单实例后台发送指令。后台串行执行驱动、解锁、遥测和省电操作；关闭前台不会停止后台。

配置位于 `C:\ProgramData\CMP90HX\settings.json`：

```json
{
  "Control": {
    "Backend": "Session",
    "ConservativeTiming": false,
    "UnlockOnStartup": true,
    "UnlockOnResume": true,
    "ReadUnlockOnConnect": true
  }
}
```

设置按钮位于主窗口右上角“一键卸载”右侧。保守兼容模式仅影响 Native。设置窗口的模式按钮点击即切换到另一种模式并保存，无需再次点击保存设置。后台先卸载旧方式的解锁驱动，再保存选择；失败时保留原选择并显示原因。旧 Native 服务已标记删除（1072）视为卸载已提交，允许保存新模式。新方式的驱动需手动安装。已加载的 Native / Read 驱动卸载需要重启生效。

驱动管理分别显示 Read、Native、Session 安装情况。点击当前方式可打开设置。“安装读取驱动”只安装 Read；“安装解锁驱动”再次确认后只安装当前选择的解锁驱动。证书也由用户手动安装。状态读取、自动解锁、手动解锁和唤醒均不会补装驱动。

Session 手动安装只将包加入 Windows 驱动仓库，不立即替换 NVIDIA。解锁仅使用与当前包哈希匹配的已安装 INF / SYS / CAT，临时接管后恢复原 NVIDIA。存在 session-restore.json 恢复记录时拒绝卸载 Session，先使用配套 worker 的 session-restore 恢复 NVIDIA。

Read 驱动安装不检查内核 DMA 保护，仅检查文件、签名和服务。未签名时显示明确的文件路径与签名后更新哈希步骤。\n\n环境检查只查询系统内核 DMA 保护状态，不执行 DMA 映射或 GPU 测试。Native 驱动安装前、Native 解锁前再次查询；开启或无法确认时拒绝使用 Native。Session 和 Read 不因内核 DMA 保护开启而被禁止安装。

查询使用 NtQuerySystemInformation 的 SystemDmaGuardPolicyInformation (202)，对应 BOOLEAN DmaGuardPolicyEnabled。此信息类来自 [System Informer phnt](https://github.com/winsiderss/systeminformer/blob/master/phnt/include/ntexapi.h)，并非稳定的 Win32 合约；系统不支持或返回格式异常时显示“无法确认”，不会视为关闭。用户可在 [系统信息 msinfo32](https://learn.microsoft.com/en-us/windows/security/hardware-security/kernel-dma-protection-for-thunderbolt) 中核对。

后台使用 PowerRegisterSuspendResumeNotification 监听睡眠唤醒。回调仅设置标志，工作线程按配置执行解锁，重复通知去重。注册失败会记录错误并显示在设置窗口，不创建替代调度任务。

程序不再提供内置开机启动注册。外部启动后台时可带 `--startup`，后台按 UnlockOnStartup 和 Power 配置执行。普通启动不会直接解锁。原版本已创建的系统调度项不会被这一版代码自动清理，如存在请在 Windows 中手动移除。

```powershell
.\CMP90HXBackground.exe --startup
.\CMP90HXBackground.exe --request Status
.\CMP90HXBackground.exe --request Unlock
.\CMP90HXBackground.exe --request PowerDisable
.\CMP90HXBackground.exe --request Shutdown
```

Shutdown 在已提交操作完成后释放省电并退出。升级时先用旧版后台的 Shutdown 指令正常退出，再启动新版。IPC 协议版本为 2，拒绝连接旧版后台，硬件请求不会自动重发。

后台日志在 ProgramData/CMP90HX/Logs；前台日志在用户 LocalAppData/CMP90HX/FrontendLogs。一键卸载恢复性能，移除三种驱动及签名根证书，停止后台；保留配置与日志，不安排重启后清理。

前后台及驱动包编译检查不能代替配套签名驱动的实机解锁、睡眠唤醒和 NVIDIA 负载验证。