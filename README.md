# 90HXWindowsUnlock

CMP 90HX (`10de:220d` / GA102) 纯 Windows 解锁实验项目，目标仓库：
https://github.com/Qi-2007/90HXWindowsUnlock

当前源码位于上述独立仓库的 `master` 分支。

从 `Qi-2007/CMP90HX-WindowsUnlock` 的 Windows 平台代码及本次新增 DMA 驱动拆分。
专用 Windows 驱动统一提供 DMA 内存、PCI 配置访问和寄存器访问，不包含 EFI DMA 引导组件、不读取 EFI 预留变量。
冷启动直接进入 Windows。`prepare-core.ps1` 可以从用户本地 EFI 文件静态提取核心；
提取不执行 EFI，也不要求通过 EFI 启动。

**当前仍是实验原型。** 已完成 WDK 编译、测试签名和驱动加载。
2026-10-02 在一台 90HX 测试机上，驱动 DMA 后端通过双向 GPU DMA 探针、
保守时序解锁以及 NVIDIA 接管后的 3 次 Code 0 / Gen2 x16 / override 验证。
该机器启动时仍执行仅预留内存的 EFI；本后端不读取其预留变量，但尚不能据此宣称
已验证完全不运行 EFI 的启动环境，也未完成长时间负载稳定性测试。
驱动提供物理地址，尚未支持 IOMMU DMA 重映射；完整解锁还依赖固定版本的原生核心。
当前源码使用 CMP90HXDma 协议 v2，已移除 WinRing0 和 ThrottleStop 运行依赖。只下载本仓库即可编译新 DMA 驱动；
构建 C# 模拟测试不要求核心文件，实际解锁需要自行准备全部依赖。

**v2 整合尚未实机验证。** 上述历史硬件结果属于旧后端。新构建必须重新签名；
旧驱动不支持新接口，若已加载 v1，须重启后安装/启动 v2。签名后运行
`update-release-hashes.ps1` 自动更新工作进程及驱动哈希并重建 GUI；发布脚本也会自动
执行这一步。旧发布包保持原样。

v2 保留管理员/SYSTEM ACL、单进程独占及 DMA 崩溃隔离。PCI 读取用于 segment-0 枚举；
写入仅允许选定 90HX 的 D0/Command、原 BAR 恢复和链路控制，以及直连上游桥的
SBR/ASPM/目标速率控制。MMIO 限于已验证 GA102 的 16 MiB BAR0、ACPI MCFG 推导
并与 HAL 对照的桥配置窗口，以及自身 DMA 内存的只读回查。`--drivers` 参数兼容保留，
不再加载参考驱动；NVIDIA 显示驱动、NVAPI/NVML 及原生核心仍是独立依赖。

在 Visual Studio x64 开发者 PowerShell 中运行 `tools/test-unified-driver.ps1`，
可验证实际内核 IOCTL 处理函数的模拟 PCI/MMIO/MCFG、包长度、越界拒绝、原 BAR 恢复、
桥控制字写入及 DMA 回查；不加载驱动或访问真实硬件。`build.ps1` 另运行用户态和核心模拟测试。

## 文件

- `driver/`：WDM x64 驱动、固定 IOCTL 协议和主机状态机测试。
- `src/`：Windows C# 程序、DMA 后端、解锁/验证和 GPU DMA 探针。
- `ui/`：C# / WPF 控制台，Windows 11 风格界面。
- `build-gui.ps1` / `run-gui-workflow.ps1`：界面构建与按需提权工作流。
- `build-dma-driver.ps1`：编译 `CMP90HXDma.sys`。
- `build.ps1`：编译 C# 程序并运行模拟自测。
- `update-release-hashes.ps1`：校验签名、构建自测、更新两个运行文件哈希并重建 GUI。
- `install-dma-driver.ps1`：检查签名并创建、启动按需驱动服务。
- `run-full-test.ps1`：Preflight / GpuDmaInitTest / Unlock / Verify。
- `prepare-core.ps1`：准备固定哈希的原生核心；`prepare-reference-drivers.ps1` 仅供旧版本研究使用。

## Windows 编译

安装 Visual Studio 2022 的 C++ 桌面开发、Windows SDK 和匹配的 WDK，
确认安装 `WindowsKernelModeDriver10.0` 平台工具集。
同时安装 WDK 的 Visual Studio 集成扩展（只有 Windows SDK 不提供内核驱动头文件）。
使用 VS 2022；构建脚本默认查找 MSBuild 17，不再使用 PATH 中可能来自其他 VS 版本的 MSBuild。

在 VS 开发者 PowerShell 中进入仓库目录：

```powershell
git clone https://github.com/Qi-2007/90HXWindowsUnlock.git 90HXWindowsUnlock
cd 90HXWindowsUnlock
.\build-dma-driver.ps1
.\build.ps1
```

脚本自动选择同时具有 SDK 用户头文件和 WDK 内核头文件/库的版本，
可用 `-SdkVersion 10.0.xxxxx.0` 指定已安装版本。
如需指定其他工具链，可通过 `-MSBuild '完整路径\MSBuild.exe'` 指定。
生成 `build\dma-driver\CMP90HXDma.sys` 和 `build\CMP90HXGen2.exe`。
构建不自动签名、不加载驱动、不修改系统设置。编译失败时保留完整日志供修正。

## Visual Studio 中打开项目

不要在源码目录重新创建空项目。打开仓库自带的 `90HXWindowsUnlock.sln`，
解决方案包含 `CMP90HXDma`（C 驱动）、`CMP90HXWindows`（命令行）和 `CMP90HXControl`（WPF 界面），源码已纳入项目。
选择 `Release | x64`。C# 项目需 .NET Framework 4.8.1 Developer Pack（含 targeting pack）；
`build.ps1` 和 `build-gui.ps1` 使用 MSBuild 及 4.8.1 引用程序集编译；前者运行模拟自测。运行程序需安装 .NET Framework 4.8.1。
直接点击 VS 生成不会自动运行模拟自测，请再执行 `build.ps1`。

如果报 `ntddk.h` 找不到，检查 WDK 和 VS 集成；不要只手动加入 km include 路径。
驱动属性应显示：配置类型 Driver，平台工具集 WindowsKernelModeDriver10.0，
Windows SDK 版本与 WDK 版本一致。日志若只有 `/MD /EHsc` 且没有内核 include 路径，
说明 WDK 构建配置尚未生效。重新打开 VS 2022 下的自带解决方案。
项目还会检查 `DDK_LIB_PATH`，在 WDK 集成未生效时提前给出明确错误。

## 图形控制台和独立运行包

`CMP90HXControl` 为 C# / WPF / .NET Framework 4.8.1 程序。启动时一次请求 UAC，硬件工作进程继承管理员权限，后续操作不再反复提权。
GUI 的文件检查、设备枚举与启停、驱动安装和启动、状态读取、预检、解锁编排、恢复与一次验证均已移植到 C#；运行时不启动 PowerShell。
硬件核心继续在独立 `CMP90HXGen2.exe` 中运行，避免核心异常直接破坏 GUI 进程。

构建 GUI：

```powershell
.\build-gui.ps1
.\build\CMP90HXControl.exe
```

构建独立包：

```powershell
.\build-release.ps1
```

哈希配置在 `ui/NativeWorkflow.cs` 的 `RuntimePaths.Gen2Hash` 与 `DmaHash`。
签名后可单独运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\update-release-hashes.ps1`，
脚本确认工作进程构建自测通过、驱动签名有效后，更新两项哈希，复制驱动到 GUI 使用的
`build\dma-driver\CMP90HXDmaSigned.sys` 并重建 GUI。默认保留并读取上次选定的
`build\dma-driver\CMP90HXDmaSigned.sys`；该文件不存在时使用 `CMP90HXDma.sys`。
重新编译并签名新驱动后，须显式传 `-SignedDriverPath` 选择新文件，避免误用旧构建。
自定义签名文件可传 `-SignedDriverPath '完整路径\驱动.sys'`。
`build-release.ps1` 自动调用同一脚本，并验证发布包内两个文件与配置哈希一致；
无效签名会在更新配置前报错。脚本不重新编译驱动、加载驱动或修改系统签名策略。

如果在打包完成后给发布目录里的 `CMP90HXControl.exe` 签名，需再运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\update-package-hashes.ps1 -PackageDirectory '.\dist\CMP90HX-Control-1.2.2-<时间>'
```

脚本验证签名及原 ZIP 中的程序内容，仅更新 GUI 签名带来的哈希变化，运行发布包校验，
再生成新的 `-signed-<时间>.zip` 和 SHA256 文件。保留原 ZIP 供对照。
工作进程或驱动发生变化时应重新运行 `build-release.ps1`。

使用本地已签名的 DMA 驱动及 `build/dma-driver/cert/` 两个证书作为输入，生成 `dist/CMP90HX-Control-1.2.2-<时间>/` 和 ZIP、SHA256。
包内包含 GUI、预编译工作进程、固定核心及统一驱动；解压后不依赖源码目录或开发工具。
构建脚本运行原有核心自测和原生编排测试，并从发布目录执行不加载驱动的完整文件检查。
开发构建仍需要 Visual Studio MSBuild；运行包需要 .NET Framework 4.8.1。

| 操作 | 内容 |
| --- | --- |
| 环境检查 | 文件与签名、核心模拟测试、系统根证书、驱动安装状态、DMA 映射/物理回读和只读硬件预检。 |
| 驱动管理 | 独立窗口查看和安装/卸载两个固定指纹根证书及 DMA 驱动；已加载驱动卸载后重启生效。 |
| 计划任务管理 | 独立窗口安装/卸载/手动触发 SYSTEM 开机及 Kernel-Power 107 唤醒任务；安装前自动补齐证书和驱动并通过环境检查。 |
| 空闲省电 | 独立窗口设置空闲 P8、GPU 阈值、等待时间及全速应用例外；SYSTEM 后台常驻，解锁前暂停，设备恢复后重新检测。 |
| 刷新解锁状态 | 确保统一驱动运行，一次只读快照，同时判断两端 PCIe Gen/宽度、计算和图形解锁寄存器；不申请 DMA 内存或改变显卡启停状态。 |
| 开始解锁 | 默认快速时序，保留保守兼容开关；原本启用的设备暂时停用，恢复后以一次快照验证。 |


日志保存在 `%ProgramData%/CMP90HX/Logs/<session-id>/`，GUI 和后台任务启动时清理旧日志，保留正在运行的会话。状态面板仅使用本次快照；
“已解锁”表示计算/图形寄存器符合固定核心预期，未进行负载性能测试。
驱动保留到本次启动结束；旧服务若运行中，先重启再切换签名驱动。
详细说明见 [运行包使用说明](docs/RELEASE_GUIDE_ZH.md)。

自动解锁任务取消原来的开机 20 秒、唤醒 10 秒延迟，直接触发后等待设备就绪；更新后重新安装任务生效。
内置省电默认关闭，启用前退出 Inspector 的 Multi Display Power Saver；启用会停用其已识别的登录任务。
通过系统 NVAPI 设置 P8 限制，GPU/视频负载或应用例外触发时恢复自动性能策略。该驱动私有接口的真实写入及睡眠恢复尚待实机验证。

`--power-monitor` 在创建 WPF 应用前进入后台循环，不加载界面，也不运行解锁状态读取或核心/参考驱动检查。桌面重复启动会恢复并聚焦已有 GUI；全局互斥锁同时允许 SYSTEM/管理员访问。卸载或更新任务会清理未被任务或进程使用的后台副本，活动副本在后续启动时继续清理。

开发者可以使用纯示例数据预览，工作流按钮禁用：

```powershell
.\build\CMP90HXControl.exe --preview success
.\build\CMP90HXControl.exe --preview failure --compact --snapshot "$PWD\build\gui-failure.png"
```

`--validate-package <报告路径>` 只运行文件校验和模拟测试，不请求 UAC 或查询显卡；用于发布包检查。

## 签名和加载

自签名证书适合测试模式；导入公开证书本身不能替代内核签名策略。
步骤见 [测试签名](docs/TEST_SIGNING_ZH.md)。签名完成后管理员 PowerShell 执行：

```powershell
.\install-dma-driver.ps1 -DriverPath "$PWD\build\dma-driver\CMP90HXDma.sys"
```

驱动服务为 `CMP90HXDma`，按需启动。每次新冷启动手动执行 `sc.exe start CMP90HXDma`。
本次启动不支持卸载；更新前先完全关机重启，在服务未启动时替换 `.sys`。

在已临时关闭驱动签名强制验证的本地、可重启测试机上，可对未签名的构建执行
不依赖显卡的加载与 DMA 内存预留/映射冒烟测试（管理员 PowerShell）：

    .\build.ps1
    .\install-dma-driver.ps1 -DriverPath "$PWD\build\dma-driver\CMP90HXDma.sys" -UnsignedSmokeTest

该开关只允许签名有效或完全未签名的文件，不接受无效签名；不会修改 Windows 启动策略。
脚本依次运行 arena-info 和 arena-reserve-test，后者申请 32 MiB 物理内存并映射至测试进程，
不启动 GPU DMA，也不验证显卡可访问该物理地址。完成后驱动和物理内存留到本次启动结束。
如果服务已存在，脚本不会覆盖；先检查 sc.exe query CMP90HXDma 的状态。

## 先验证 DMA，再测试解锁

安装/启动重新签名的 CMP90HXDma v2 后，首先在管理员终端测试 CPU 映射：

```powershell
.\build\CMP90HXGen2.exe arena-info --physical-dma-experiment
.\build\CMP90HXGen2.exe dma-test --drivers .\drivers --physical-dma-experiment --log .\logs\dma-test.log
```

完整探针/解锁需要固定原生核心。使用自己的 UEFI v0.2.2 / core 469dc0c 文件静态提取：

```powershell
.\prepare-core.ps1 -EfiPath 'D:\local\NVPermissiveEFI.efi'
.\build.ps1
```

可用 `-Python` 指定 Python 3 路径。新仓库不分发上游核心或第三方二进制。
确认唯一 90HX 已停用 Code 22，然后执行：

```powershell
.\run-full-test.ps1 GpuDmaInitTest -PhysicalDmaExperiment
```

必须看到 `GPU_DMA_ROUNDTRIP_VERIFIED`，并确认恢复状态。失败保持设备停用；
不自动重试、不自动进入解锁。成功后才运行：

```powershell
.\run-full-test.ps1 Unlock -PhysicalDmaExperiment
```

原本停用的设备仍保持停用。手动启用后使用 `Verify -PhysicalDmaExperiment` 检查
NVIDIA 接管，区分停用态成功和 Code 0 下的 Gen2/override 验证。
日志位于 `logs/`。

## DMA 生命周期和限制

驱动申请低于 4 GiB 的 32 MiB 连续、缓存一致物理内存，并映射到所属 x64 进程。
不接受任意物理地址、大小或用户地址；只允许 SYSTEM/管理员、单一打开者。
底层内存保留到本次 Windows 启动结束。正常原生 free 完成后允许复用，不释放物理页。
DMA reserve 的物理页由驱动持有；如果预留后卸载并释放，仍在访问原地址的设备可能写入
被系统重新分配的内存。因此当前不在预留完成后自动卸载，也不调用 sc.exe stop。

写入首块 DMA 缓冲区前 ARM，所有原生 free 完成后 COMPLETE。
句柄关闭或进程崩溃时若仍是 ARMED，驱动撤销用户映射并进入 QUARANTINED；
后续映射/覆盖被拒绝，删除日志不能清除内核状态。保持 GPU 停用并完全关机重启。
COMPLETE 依赖现有原生核心停止 DMA 的契约，驱动不自行读 GPU 状态证明停止。

这是物理地址实验路径，不注册 90HX 的 DMA adapter，也不创建 IOMMU IOVA。
`-PhysicalDmaExperiment` 不检测或改变 VT-d、内核 DMA 保护等平台条件。
连续低地址分配可能因内存碎片失败，不使用未知物理页替代。
GUI 可安装唤醒后执行的任务，但已映射期间的睡眠、休眠及 Fast Startup 仍未验证；本次没有修改内核电源管理，也尚未完成唤醒后的实机测试。

## 可运行的主机测试

Linux 上可验证共享状态机，不能验证 WDK/MDL 或 GPU DMA：

```sh
gcc -std=c11 -Wall -Wextra -Werror driver/tests/state_test.c -o /tmp/cmp90hx-state-test
/tmp/cmp90hx-state-test
```

平台代码的授权条款见 [LICENSE](LICENSE)，上游依赖范围见 [第三方说明](THIRD_PARTY_NOTICES.md)。
