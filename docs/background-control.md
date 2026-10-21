# 前后台与驱动管理

前台通过共享内存向单实例后台发送指令。后台串行执行驱动、解锁、遥测和省电操作；关闭前台不会停止后台。

配置位于 `C:\ProgramData\CMP90HX\settings.json`：

```json
{
  "Control": {
    "Backend": "Session",
    "StartOnBoot": false,
    "ConservativeTiming": false,
    "UnlockOnStartup": true,
    "UnlockOnResume": true,
    "ReadUnlockOnConnect": false
  }
}
```

设置按钮位于主窗口右上角“一键卸载”右侧。保守兼容模式仅影响 Native。设置窗口的模式按钮点击即切换到另一种模式并保存，无需再次点击保存设置。后台先卸载旧方式的解锁驱动，再保存选择；失败时保留原选择并显示原因。旧 Native 服务已标记删除（1072）视为卸载已提交，允许保存新模式。新方式的驱动需手动安装。已加载的 Native / Read 驱动卸载需要重启生效。

驱动管理分别显示 Read、Native、Session 安装情况。点击当前方式可打开设置。“安装读取驱动”只安装 Read；“安装解锁驱动”再次确认后只安装当前选择的解锁驱动。证书也由用户手动安装。状态读取、自动解锁、手动解锁和唤醒均不会补装驱动。

Session 手动安装只将包加入 Windows 驱动仓库，不立即替换 NVIDIA。解锁仅使用与当前包哈希匹配的已安装 INF / SYS / CAT，临时接管后恢复原 NVIDIA。存在 session-restore.json 恢复记录时拒绝卸载 Session，先使用配套 worker 的 session-restore 恢复 NVIDIA。

Read 驱动安装不检查内核 DMA 保护，仅检查文件、签名和服务。未签名时显示明确的文件路径与签名后更新哈希步骤。\n\n环境检查只查询系统内核 DMA 保护状态，不执行 DMA 映射或 GPU 测试。Native 驱动安装前、Native 解锁前再次查询；开启或无法确认时拒绝使用 Native。Session 和 Read 不因内核 DMA 保护开启而被禁止安装。

查询使用 NtQuerySystemInformation 的 SystemDmaGuardPolicyInformation (202)，对应 BOOLEAN DmaGuardPolicyEnabled。此信息类来自 [System Informer phnt](https://github.com/winsiderss/systeminformer/blob/master/phnt/include/ntexapi.h)，并非稳定的 Win32 合约；系统不支持或返回格式异常时显示“无法确认”，不会视为关闭。用户可在 [系统信息 msinfo32](https://learn.microsoft.com/en-us/windows/security/hardware-security/kernel-dma-protection-for-thunderbolt) 中核对。

后台使用 PowerRegisterSuspendResumeNotification 监听睡眠唤醒。回调仅设置标志，工作线程按配置执行解锁，重复通知去重。注册失败会记录错误并显示在设置窗口，不创建替代调度任务。

解锁状态只在手动点击“刷新解锁状态”或解锁完成验证时采集，不再启动时或定时自动采集。旧配置的 `ReadUnlockOnConnect` 字段保留兼容，但不再启用自动采集。

设置窗口分为“解锁方式”和“后台自动操作”两张卡片。勾选“开机自启后台”并保存，会将完整运行包校验后复制到 ProgramData/CMP90HX/AutoUnlock 的受保护版本目录，注册 `CMP90HX Background` 开机任务，以 SYSTEM 身份运行 `CMP90HXBackground.exe --startup`。默认不注册。取消勾选并保存会移除该任务，当前后台继续运行；一键卸载也会移除该任务。任务更新或配置保存失败会显示错误，配置保存失败时尝试恢复原任务。

“开机启动时自动解锁”与后台自启独立：未勾选自动解锁时，开机后台仍可按已保存的省电和唤醒设置工作。手动打开前台启动后台不附加 `--startup`，不会直接解锁。注册自启不会安装驱动或立即执行解锁。旧版本的自动解锁任务不在新任务的管理范围内。

检测到旧版内置 `CMP90HXControl.exe --power-monitor` 或归属 `CMP90HX.Control` 的 `CMP90HX Idle Power Saver` 任务时，后台自动迁移：先保存原省电配置和迁移记录，再移除旧省电任务、临时停用旧控制器，等待它释放限制并退出，随后恢复原设置并接管。迁移中断会保留记录并自动重试。只清理已识别旧省电运行目录中不再被其他任务或进程引用的文件；保留配置、驱动和其他功能数据。不会清理 Inspector 或旧自动解锁任务。旧 `Power/settings.json` 与 `Power/status.json` 会迁移到统一配置及 State 目录，保留异常恢复所需的限制归属。

```powershell
.\CMP90HXBackground.exe --startup
.\CMP90HXBackground.exe --request Status
.\CMP90HXBackground.exe --request Unlock
.\CMP90HXBackground.exe --request PowerDisable
.\CMP90HXBackground.exe --request Shutdown
```

Shutdown 在已提交操作完成后释放省电并退出。启动新版时检测到协议 2 的旧后台，会自动调用新版 `CMP90HXBackground.exe --request Shutdown`，等待旧进程实际退出后再启动新版后台。多个启动请求会串行处理；已经是当前版本则复用，检测到更新版本不会降级替换。退出超时或请求失败会报告原因，不会强制终止硬件操作或并行启动另一个后台。IPC 协议版本为 2，同时校验后台功能版本；硬件请求不会自动重发。升级后重新保存开机自启设置，将任务指向新版运行包。

主窗口和驱动管理中的 Session 驱动正常安装时仅显示“已安装”，临时接管说明保留在驱动管理的说明区域。设置和驱动管理均保留与主窗口一致的水印，水印不拦截点击。

后台日志在 `%ProgramData%\CMP90HX\Logs`，前台日志在 `%ProgramData%\CMP90HX\FrontendLogs`。启动会清理已退出会话的普通日志，正在使用的日志由租约保护。操作失败时，将异常、操作输出和对应目录中的日志、快照复制到独立的 `%ProgramData%\CMP90HX\ErrorLogs\<UTC时间-唯一编号>`。后台和前台启动失败也会生成错误归档；同一轮连续省电故障去重。最多保留最新 10 份，成功保存第 11 份时才清理最旧记录，不按天数删除。

一键卸载恢复性能、移除三种驱动并停止后台；仅使用 -EnableRootCertificates 编译的版本会额外移除构建指定的根证书。退出后清理全部应用数据，包括 `ErrorLogs` 中的错误归档；被占用文件安排重启后删除。日常启动不删除错误归档，保留最新 10 份的规则只用于正常运行期间。

实时省电策略、GPU 负载、功耗和 P-state 通过共享内存传给前台，不再逐次采样写 JSON。`State/power-status.json` 仅保存恢复所需的限制归属：施加 P8 前先持久化，成功解除后再清除；用户省电设置仍保存在统一配置中。即使后台异常退出，下次启动仍可恢复自动性能。

前后台及驱动包编译检查不能代替配套签名驱动的实机解锁、睡眠唤醒和 NVIDIA 负载验证。

后台独立线程约每秒只读查询 PnP 和驱动服务状态，解锁期间也会更新设备卡片；前台每秒读取共享内存。左侧“刷新”直接显示最新发布状态，不排队等待解锁，不采集解锁寄存器，不启停显卡，不安装驱动。显示真实采集时间；设备切换时若查询失败或数据过期，标明显示上次状态。
