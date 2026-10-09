# 使用说明

完整解压发布包，双击 CMP90HXControl.exe 并授权管理员权限。首次使用先进行环境检查，在右上角设置中选择 Native 或 Session，然后在驱动管理中手动安装签名证书、读取驱动及所选解锁驱动。

开启内核 DMA 保护时不能使用 Native，请选择 Session。解锁驱动安装有二次确认；任何读取或解锁操作均不自动补装驱动。Session 安装先加入驱动仓库，解锁时临时接管，完成后恢复 NVIDIA。

切换方式先卸载旧方式的驱动，新方式需手动安装。读取驱动可共用。已加载的 Native / Read 更新或卸载需要重启。不要混用不同包的 worker 和 SYS；签名后重新生成哈希并构建 Console。

后台直接监听唤醒通知，失败时报告错误。设置中的自动操作仅作用于已启动的后台；本版不注册系统调度项，也不自动迁移历史调度项。

一键卸载会恢复自动性能，卸载三种驱动和签名根证书，完成后退出；保留配置和日志。存在 Session 待恢复记录时，先用对应 worker 的 session-restore 恢复 NVIDIA。

详情见 [后台与配置](background-control.md)、[读取驱动](shared-reader.md)、[独立后端包](backend-packages.md)。