# 共用只读状态驱动

Native 解锁仍由原 CMP90HXDma 负责，包括解锁事务内部的 PCI/MMIO 读写、复位和 DMA。Session 解锁仍使用临时 CMP90HXUnlockSession，完成后恢复 NVIDIA。

两种方案的日常 Status/Verify、Session 自动解锁前查询及解锁后查询、Console 中 Native 解锁前后的外部状态快照，统一使用 CMP90HXReader。Native worker 内部事务采样保留，不能删除解锁过程依赖的内部读取。

读取流程：前台 → 共享内存 → 后台串行队列 → 独立 CMP90HXReader.exe → CMP90HXReader.sys → CPU MMIO → 当前 JSON 快照。新驱动不是 Filter，不替换 NVIDIA，不使用 GPU DMA。

## 接口边界

新驱动仅提供一个固定快照 IOCTL：输入 segment 0 的 GPU BDF，输出 64 字节协议 v1。驱动验证 10DE:220D、唯一直接上游桥、BAR0、D0、Memory Decode、GA102 BOOT0；只映射 BOOT0 所在页和 0x823000 寄存器页，并使用 PAGE_READONLY。固定读取 0x82381c、0x823820、0x823830，以及 GPU/桥 PCIe Link Status。没有任意物理地址、任意寄存器偏移、写入、复位、固件或 DMA 接口。

每次请求重新发现拓扑并映射，结束前复核 BAR、设备身份、桥路由、PCIe capability 及 Command，然后释放映射。后台仅在 Code 0 时读取，解锁和读取串行，失败时清除有效快照。异常寄存器值表示无法确认，不能据此认定解锁。

此独立驱动不在显卡 PnP 栈中：前后检查无法消除外部设备移除、睡眠或 NVIDIA 自行复位与 MMIO 访问之间的所有竞争窗口。它继承旧版 segment-0 PCI/MMIO 方式；目标 Windows 平台若限制此类 PCI 访问，会返回失败，不尝试绕过。编译通过不能作为睡眠唤醒稳定性结论，需签名后实机确认。

管理员/SYSTEM 控制设备 ACL，加上专用客户端精确 SHA256 校验。后台先启动驱动，再启动新的客户端子进程，以匹配进程创建验证机制。前台不直接访问驱动。

## 构建、签名和测试

交付位置固定为 Console 的 `build/reader-driver`：CMP90HXReader.exe、CMP90HXReader.sys。驱动源码在 reader/driver，客户端源码在 reader/client。

```powershell
cd E:\code\90HX\90HXWindowsUnlock\90HXWinUnlockConsole
.\build-reader-driver.ps1
# 使用自己的签名流程签 build\reader-driver\CMP90HXReader.sys。
.\build-reader-driver.ps1 -UpdatePinsOnly
# 若已有后台运行，先在管理员终端请求正常退出，并关闭前台：
.\build\CMP90HXBackground.exe --request Shutdown
.\build-gui.ps1 -NonInteractive
.\build\CMP90HXControl.exe
```

前台驱动管理的“安装读取驱动”只安装/启动已签名的共用读取服务。“安装解锁驱动”二次确认后只安装所选方式的驱动。读取及解锁不会自动补装驱动。选择 Session 不会安装 Native DMA 驱动或 Lower Filter。服务将 SYS 放在受保护的 ProgramData/CMP90HX/ReaderStore/哈希目录。

新驱动不支持运行中卸载，更新已加载 SYS 时需重启；不能只替换文件。驱动签名后必须更新 ReaderHash 并重新编译前后台。若要签名读取 EXE，应先签 EXE，再生成其哈希并编译 SYS，不能在 SYS 生成后改变客户端。

安装之后，可先做单次只读采集，再测试两种方案的界面：

```powershell
.\build\reader-driver\CMP90HXReader.exe --bdf 02:00.0 --out .\build\reader-status.json
```

核对锁定/解锁时的原始值、显示时间和 BDF；之后在实际睡眠唤醒、切换驱动和 NVIDIA 正常负载期间确认读取行为。不能将上次事务结果代替当前快照。

尚未自动签名、安装、加载或测试显卡。构建已验证 SYS/客户端配对和前后台编译；导入表未包含 DMA、PCI 写入及设备栈挂接接口。
