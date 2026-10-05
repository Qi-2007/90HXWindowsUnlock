# CMP 90HX 光追研究：2026-10-04 只读基线

## 结论

当前测试机的 CMP 90HX **已经向 D3D12 报告 DXR Tier 1.0**。这更新了交接中的 API 能力未知状态，但不证明 RT 硬件启用、光追执行正确或性能恢复。尚不能判断恢复硬件性能是否必须修改 NVIDIA 驱动。

本轮没有写 GPU MMIO/PCI、停用设备、运行光追工作负载、修改或安装任何驱动。新增文件仅为独立研究工具和本记录，没有修改 GUI、解锁、省电、计划任务或发布构建。没有 commit/push。

## 实测环境

- 用户提供的 `nvidia-smi -q`：616.56，KMD 616.56，WDDM，Ampere，`00000000:02:00.0`，PCI `220D10DE`，VBIOS `94.02.74.00.07`。
- 本机实时 PnP 查询：`NVIDIA CMP 90HX (RainCandy Technology)`，`32.0.16.1656`，`oem14.inf`。
- 运行中 `nvlddmkm` 服务的路径：`C:\Windows\System32\DriverStore\FileRepository\nvac.inf_amd64_786a7910b35feb43\nvlddmkm.sys`。
- `CMP90HXDma` 服务运行中；当前工具进程不是管理员。
- INF 实际文本：RainCandy Technology Modified Edition (2026/08/26)，DriverVer `08/20/2026, 32.0.16.1656`。仅凭版本号不能断言与文档的 20260925 安装包二进制完全相同。

已复制 `nvlddmkm.sys`、`nvwgf2umx.dll`、`nvrtum64.dll`、`nvapi64_impl.dll`、`nvac.inf` 到忽略目录 `drivers/rt-research-616.56/`，逐个校验源文件和副本 SHA256 一致。清单在 `logs/rt-research-20261004/driver-manifest.json`。

`nvlddmkm.sys` SHA256：`FA50052C86F347EBD4267191B3F242E597D4620DD4FB464DC6E1D8A30699639B`。

Authenticode 报告四个 PE 文件签名有效：内核文件签署者 Micro-Star International，`nvwgf2umx.dll` 为 Wuhan Hemei Fuxing Technology，`nvrtum64.dll` 和 `nvapi64_impl.dll` 为 NVIDIA。签名结果不说明具体 patch 内容，也不构成与 NVIDIA 原版二进制的对照证据。INF 的独立 Authenticode 查询为 UnknownError，不能替代目录签名验证。

## DXR 查询

独立 `tools/ReadOnlyRtProbe.cs` 使用 DXGI 枚举，严格选择 PCI `10de:220d`，不会使用默认 Intel GPU 或 WARP 结果替代。基线目标 LUID `00000000:00b3322c`。

```text
D3D12CreateDevice(FL11_0) HRESULT=0x00000000
CheckFeatureSupport(OPTIONS5) HRESULT=0x00000000
RaytracingTier=10 (1.0)
RenderPassesTier=0
SRVOnlyTiledResourceTier3=1
```

原始记录：`logs/rt-research-20261004/dxr-baseline.txt`；第二次查询结果一致，记录在 `mmio-baseline.txt`。OPTIONS5 的 HRESULT 和字段值分别记录，查询失败不会被转换为 NOT_SUPPORTED。

方法依据：[Microsoft OPTIONS5 文档](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_feature_data_d3d12_options5)、[Microsoft DirectX-Headers](https://github.com/microsoft/DirectX-Headers/blob/main/include/directx/d3d12.h)。Tier 报告属于驱动/API 能力层；不包含加速结构构建、DispatchRays 正确性或硬件吞吐测试。

## MMIO 未采集成功

`--read-mmio` 在打开 `\\.\CMP90HXDma` 时收到 Win32 拒绝访问，未读取任何 GPU 寄存器。不能把缺失记录解释为寄存器为零。

已检查当前驱动源码：`driver/src/driver.c` 的设备 ACL 为 `D:P(A;;GA;;;SY)(A;;GA;;;BA)`，仅 SYSTEM 和管理员可访问。`driver/src/hardware.h` 的 BindHardware 验证设备、桥和 BAR 后建立映射，不写 GPU/PCI；专用工具只允许 INFO、BIND、PCI_READ、MMIO_READ，未调用 DMA 映射/arm、write IOCTL 或现有解锁流程。BIND 会更新驱动内部绑定和映射状态，因此这里的“只读”指不写硬件寄存器。

工具仅采集 `0x823808` 和已经使用的三个 speed selector；没有采集猜测的制造 fuse 或 READOUT 地址，也不按旧注释解码 SM_TTU。

在测试机的**管理员 x64 PowerShell**中运行：

```powershell
Set-Location 'E:\code\90HX\90HXWindowsUnlock\90HXWindowsUnlock'
.\tools\ReadOnlyRtProbe.ps1 -ReadMmio
```

默认输出到新的 `logs/rt-probe-时间/`，无需安装或重编译 CMP90HXDma。省电后台可能并发访问该独占设备；如果设备忙或身份验证失败，保留错误记录，不自动暂停后台、停用 GPU 或重启。

## 静态分析的新线索

`tools/InspectRtDriver.ps1` 扫描四个副本的 PE section 内 little-endian 32 位字面量 `0x823808/14/18/28/34` 和相关 ASCII 名称。没有发现 `grGetRTCoreCount`、`SM_TTU` 或 `FEATURE_OVERRIDE_QUADRO` 字符串；这不意味着相关函数不存在，release 二进制可能没有符号。

只有 `nvlddmkm.sys` 命中一次 `0x00823814`：`.text`，文件偏移 `0x9be506`，RVA `0x9bed06`（立即数位置）。从明确的函数起点 RVA `0x9becf0` 反汇编得到：

```text
1409BECF0  sub rsp,38h
1409BECF4  add rcx,43F0h
1409BECFB  mov qword ptr [rsp+20h],0
1409BED04  mov r9d,823814h
1409BED0A  xor r8d,r8d
1409BED0D  xor edx,edx
1409BED0F  call 1401259A0
1409BED14  shr eax,10h
1409BED17  and al,1
1409BED19  add rsp,38h
1409BED1D  ret
```

**已确认的指令事实**：函数向 helper 传入 `0x823814`，随后取返回值的 bit16。尚未证明 helper 返回的是该寄存器内容、函数名、所属架构或功能。这里没有 bit9 提取证据，不能将其命名为 SM_TTU 判断或 `grGetRTCoreCount`。

Helper RVA `0x1259a0` 的前段存在对象状态分支和间接调用；还没有完成其返回值和 MMIO 路径追踪。二进制字符串包含 `r615/r616_41` 构建路径，不能拿 R515 注释直接给 R616 函数命名。

扫描完整记录：`static-scan.json`。反汇编：`readout-candidate-disasm.txt`、`register-helper-disasm.txt`，均在 `logs/rt-research-20261004/`。dumpbin 范围输出的首尾可能截断指令，研究仅使用明确起点后的完整指令。

限制：字面量扫描不覆盖地址基址加偏移、间接寻址、常量生成、其他模块或固件；section 的 executable 属性也不保证所有字节都是代码。没有发现 `0x823808` 字面量不等于驱动不消费 override。

## 下一步实验条件

1. 管理员只读基线已取得（见 15:51:06 记录），后续实验保持硬件状态与 DXR 查询时间对应。
2. 追踪 helper 和候选函数调用者、间接表引用；找到同版本 NVIDIA 原版二进制或可验证定义作对照，识别真实 RT Core 数量/能力判断。当前无法给出可用 patch 地址。
3. 独立核实 GA102 value/override/READOUT/制造 fuse 定义和初始化时序后，才设计保留其他字段的 RMW。没有确认任何光追写值，包括 `0x03000000`。
4. 即使 DXR 已报告 1.0，后续仍需要经授权的实际 DXR 正确性和吞吐实验；分别记录 AS 构建、射线执行、输出正确性及纯计算对照，再与正常 GA102 对照。不要只根据 RT 数量或 FPS 宣称恢复。
5. 保持硬件有效状态与驱动判断两条证据链独立。如果需要 patch，须定位限制源于内核、用户态、固件还是缓存，而非先修改 CMP90HXDma 或 INF。

当前未解决项：GA102 正式字段编码、制造 fuse 地址、有效 READOUT、真实 RT 性能、是否必须改 NVIDIA 驱动。`0x823808` 原值已在下述管理员采集中取得。

## 同日诊断工具修复

用户管理员运行时到达 PCI_READ IOCTL `0x8337e014` 后失败；旧错误没有 BDF 和 NativeErrorCode，因此不能确定具体失败设备或错误码。检查发现旧版桥扫描对 multifunction 设备的每个子功能直接读取 class code，没有先确认该功能存在。已改为一次枚举，逐功能只读身份确认存在后，再读取 class/routing；只容忍身份探测的 Win32 433/1167 缺失错误，其他访问和协议错误保留。对已确认设备的读取失败继续中止，不伪造寄存器值。

PowerShell 5.1 在 `$ErrorActionPreference='Stop'` 下将原生 stderr 变成终止异常，导致 `probe.txt` 未保存。已改为用隐藏的子进程分别捕获 UTF-8 stdout/stderr、退出码，异步读取两个流后等待完成；无自动提权。失败记录保存后才报告失败。

已通过 `--self-test` 验证稀疏 multifunction、身份缺失容忍、访问/协议错误传播；通过 Windows PowerShell 5.1 运行验证权限失败时仍保存完整 DXR 输出和错误文本。随后管理员 MMIO 成功路径也已通过测试机运行验证，详见下一节。

## 管理员只读采集成功：15:51:06（北京时间）

用户运行修复后的工具成功，退出码 0。原始输出在 `logs/rt-probe-20261004-155106/probe.txt`，驱动身份在同目录 `pnp-driver.json`。

```text
UTC=2026-10-04T07:51:06.7934393Z
GPU BDF=02:00.0
Bridge BDF=00:06.0
BAR0=0x80000000
BOOT0=0xb72000a1
BAR0+0x823808=0x00100283
BAR0+0x82381c=0x88888888
BAR0+0x823820=0x00000008
BAR0+0x823830=0x00000004
D3D12CreateDevice(FL11_0) HRESULT=0x00000000
CheckFeatureSupport(OPTIONS5) HRESULT=0x00000000
RaytracingTier=10 (1.0)
```

这验证了工具对当前测试机的只读 PCI/BAR0 通路可用，且三个 speed selector 符合现有解锁目标；不证明 RT 硬件状态或性能。DXR 查询先于寄存器读取，属于一次运行中的顺序采样，并非原子快照。

`0x00100283` 的 bit24/25 数值均为 0，这是原始值的算术事实；只有在旧注释中的字段位置得到独立核实后，才能赋予 SM_TTU/override 语义。即便字段位置正确，override 为 0 时也可能由制造 fuse 决定有效状态，不能从该值推出“RT 开启”或“RT 关闭”。没有确认 enable 编码或提出任何写入值。

下一步重点转为核实 GA102 正式定义并追踪实际 R616 驱动消费路径；本次已经取得的身份、DXR 和寄存器基线无需重复向用户索取。尚未执行硬件写入或真实光追测试。

随后已定位 Windows GR_GET_INFO_V2 的导出入口和信息表返回循环，细节、证据边界和后续方法见 [R616 驱动路径追踪](rt-driver-path-20261004.md)。
