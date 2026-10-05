# CMP 90HX：R616 驱动只读路径追踪

## 本轮结论

已在安装的 Windows 驱动副本中定位 GR_GET_INFO_V2 的消费者、静态信息填充器和 RT 数量的分发槽。后续已核对 GA102 内部编号 `0x2B`，其 RT 数量实现为 `0x7d3a50`，查询 `0xFF00008D`；服务查询转换为相对偏移 `0x168`、bit0，直接 MMIO 分支映射为 `BAR0+0x820168`。尚未确认运行时路由/拦截状态、override 是否能改变该输入，或 NVIDIA 用户态驱动的 DXR 能力决策。

因此，修改 override 后立即查询一个数量字段，可能仍取得此前初始化的信息；本轮证据没有证明信息表何时更新，也没有证明必须重启设备、冷启动或 patch 驱动。不要据此执行这些操作。

本轮仅分析驱动副本、下载 NVIDIA 公开源码和准备静态分析工具，没有 GPU 写入、驱动安装/修改、设备重启或真实光追测试。先前已取得的 DXR Tier 1.0 和 `0x823808=0x00100283` 基线仍有效，见 [基线记录](rt-research-20261004.md)。

## 证据身份

- Windows：安装文件 `nvlddmkm.sys`，版本 `32.0.16.1656`，副本 SHA256 `FA50052C86F347EBD4267191B3F242E597D4620DD4FB464DC6E1D8A30699639B`。
- 副本：`drivers/rt-research-616.56/nvlddmkm.sys`。
- 所有下述地址均为该文件的 **RVA**，不是当前运行驱动的绝对地址；不同文件/版本不能照搬。
- NVIDIA 公开源码固定到 commit `61dcc93722ecb418bb5f2e00923f05b4b8051dd1`。保存在 `logs/rt-research-20261004/public/`，包含文件树、SDK headers、NVOC 定义和 kernel graphics 源文件。公开 Linux 源码用于接口布局和行为对照，不冒充 Windows 源码。

## RT 数量查询的正式接口线索

NVIDIA SDK 定义：

| 项目 | 值 |
| --- | --- |
| `NV0080_CTRL_GR_INFO_INDEX_RT_CORE_COUNT` | `0x22` |
| Device `GR_GET_INFO` | `0x00801104` |
| Device `GR_GET_INFO_V2` | `0x00801110` |
| Subdevice `GR_GET_INFO` | `0x20801201` |
| Subdevice `GR_GET_INFO_V2` | `0x20801228` |

来源：[ctrl0080gr.h](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/61dcc93722ecb418bb5f2e00923f05b4b8051dd1/src/common/sdk/nvidia/inc/ctrl/ctrl0080/ctrl0080gr.h)、[ctrl2080gr.h](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/61dcc93722ecb418bb5f2e00923f05b4b8051dd1/src/common/sdk/nvidia/inc/ctrl/ctrl2080/ctrl2080gr.h)。这给出了 RT 数量字段的接口身份，不提供 GA102 fuse 编码或硬件完整性证据。

固定版本的公开 `_kgraphicsCtrlCmdGrGetInfoV2` 从 `pGrInfo->infoList[index].data` 返回数据。来源：[kernel_graphics.c](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/61dcc93722ecb418bb5f2e00923f05b4b8051dd1/src/nvidia/src/kernel/gpu/gr/kernel_graphics.c)。后续 Windows 二进制分析独立确认了相应的内存表读取行为。

## Windows 导出表与入口定位

使用 NVIDIA 的 [NVOC_EXPORTED_METHOD_DEF 布局](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/61dcc93722ecb418bb5f2e00923f05b4b8051dd1/src/nvidia/inc/libraries/nvoc/runtime.h)解析 `.rdata`。该布局的 `pFunc` 位于 `methodId` **之前 16 字节**；其后依次有 flags、accessRight、methodId、paramSize、classInfo。不能把 methodId 后 16 字节的下一条记录函数指针当成本条入口。

以下映射同时核对命令 ID、参数大小、classInfo 一致性、指针目标的 executable section 和反汇编行为：

| Method ID | 记录起点 RVA | Handler RVA | 参数大小 |
| --- | --- | --- | --- |
| `0x00801104` | `0xe56310` | `0x3d02f0` | `0x10` |
| `0x00801110` | `0xe56390` | `0x3d0070` | `0x210` |
| `0x20801201` | `0xe61ea0` | `0x3d6f40` | `0x20` |
| `0x20801228` | `0xe621a0` | `0x3d6cb0` | `0x210` |

导出记录：`logs/rt-research-20261004/nvoc-verified-path.json`。源码中相应名称为 Device/Subdevice 的 `KGrGetInfo` / `KGrGetInfoV2`；二进制没有这些函数名字符串，命名依据接口映射，不能等同于取得了其调试符号。

Subdevice V2 的返回循环（RVA）：

```text
3d6e45  lea rdx,[r14+8]          ; 首个输出 data 字段
3d6e50  mov eax,dword ptr [rdx-4]; 请求 index
3d6e53  cmp eax,3fh             ; index 范围检查
3d6e56  jae 3d6e8d
3d6e58  mov ecx,[r9+rax*8+4]    ; 信息表条目的 data
3d6e5d  inc r8d
3d6e60  mov [rdx],ecx           ; 返回给调用者
3d6e62  add rdx,8
3d6e66  cmp r8d,edi
3d6e69  jb 3d6e50
```

该循环对所有合法 index 使用同一信息表；若请求 `0x22`，数据位置相对于此处 `r9` 为 `0x22*8+4=0x114`。这是**内核对象内存偏移**，不是 BAR0 寄存器地址，不可拿来做 MMIO。

代码会依据对象状态从不同位置取得信息表：一条分支取对象 `+0x670` 的指针，另一条经路由 helper `0x686b10` 返回对象，再取得其静态信息指针。尚未动态确认当前 90HX 实际走哪条分支。完整反汇编：`subdevice-gr-info-v2-verified-disasm.txt` 和 `device-gr-info-v2-verified-disasm.txt`。

`nvapi64_impl.dll` 还出现了使用 `0x20801228`、参数大小 `0x210`、单条 index 请求的调用代码（RVA `0x5923a` 起），记录在 `nvapi-gr-query-disasm.txt`。该处 index 来自输入对象字段，未确认调用时是 `0x22`，也未确认它与 DXR 查询相关。这只能证明用户态模块存在该 GR 信息查询通路。

## 原 `0x823814` 候选函数的进展

候选 RVA `0x9becf0` 向 helper `0x1259a0` 传入地址 `0x823814`，随后 `shr eax,16`、`and al,1`。本轮完整追踪 helper 的一条分支：

```text
125afd  call ea7a0              ; r8d 保存传入地址
...
ea7d7   mov rax,[rdi]           ; 映射/地址描述对象中的基址
ea7da   mov ecx,ebx             ; 传入的字节偏移
ea7dc   shr rcx,2
ea7e0   mov eax,[rax+rcx*4]     ; 32 位映射内存读取
```

这确认 helper 存在读映射内存并返回值的路径；另有间接读取、拦截/状态分支及错误处理。尚未确认当前架构和运行状态选择的具体路径，也没有动态核实此对象映射正是 GA102 BAR0。

候选函数在 PAGE section 的 RVA `0x176d39e` 被 RIP-relative LEA 引用，随后存到表 `+0x258`；选择逻辑依赖内部编号和 mask。该内部编号尚未与 GA102 对应，也没有取得表项名称。没有发现直接调用引用并不能证明函数不会执行，因为已找到间接表注册。

本轮仍没有 bit9/SM_TTU 的匹配证据；候选不能命名为 `grGetRTCoreCount`。证据分别在 `readout-registration-disasm.txt`、`register-helper-complete-disasm.txt`、`mmio-leaf-disasm.txt`。确认读取路径**不等于**确认 `0x823814` 的正式 READOUT 字段定义，因此没有扩展真实 MMIO 采集范围。

## 静态工具与限制

- `tools/InspectRtDriver.ps1` 支持指定 32 位字面量，默认仍扫描既有寄存器候选；不写二进制。
- `tools/TraceRtDriver.ps1` 记录 rel32 call/jump、RIP LEA、绝对指针候选，并按公开布局解析上述 NVOC 方法。候选须以反汇编核对指令边界。
- `.pdata` 给出的是 unwind region；本文件同一个逻辑函数可能被分成多个 region，不能将第一条 region 的 End 当作整个函数结束。
- 目录里的 `gr-info-v2-disasm.txt`、`device-gr-info-impl-disasm.txt`、`gr-info-handlers-xrefs.json` 是早期相邻表项探索，不是最终命令映射。最终应使用 `*-verified-disasm.txt` 和 `nvoc-verified-path.json`，避免复用被排除的候选。
- Windows 副本中三个预期 NVOC 映射已用工具结果和人工检查的字节做一致性校验；没有执行任何研究地址对应的代码，也没有生成 patch。

## 下一步研究方法已收窄

1. 沿 index `0x22` 的生产者继续追踪服务查询；区分 legacy RM、本地静态数据和固件/RPC 分支。现在不应在通用返回循环中强制改数量来冒充硬件恢复。
2. 对真实生产者追踪 fuse 读取、产品判断和 RT 单元数量计算，再与同版本未修改的 Windows 文件或可靠源码对照；目前没有同版本原版二进制。
3. 验证字段编码、override 的生效方式及缓存更新时间后，才确定是否需要重初始化或修改 NVIDIA 内核/用户态驱动。没有确认 `0x03000000` 或任何光追写值。
4. 性能层仍需后续 DXR 正确性与吞吐实验，DXR 1.0 或数量字段都不能代替这层验证。

上述是第一阶段的消费者追踪。下文记录后续的生产者定位；硬件 override 定义仍未核实。

## 后续：静态信息填充器与 RT 分发

内部 NVOC 记录已按同一布局核对：

| Command | Record RVA | Handler RVA | 参数大小 |
| --- | --- | --- | --- |
| `0x20800a2a` STATIC_KGR_GET_INFO | `0xe600a0` | `0x3bd3f0` | `0xfc0` |
| `0x20800a2b` STATIC_GR_GET_INFO | `0xe600c0` | `0x3baa70` | `0xfc0` |

`0x3bd3f0` 转发到 `0x20800a2b`。`0x3baa70` 初始化每个引擎的 63 条 `(index,data)`，再调用 `0x3b3890` 填充；每引擎 `0x1f8` 字节。legacy 缓存初始化函数 `0x687d50` 从静态信息指针复制 `0x1f8` 字节到对象 `+0x670`，随后设置 `+0x678` 初始化标记。这里确认的是静态调用关系，未动态确认 90HX 的执行分支。

`0x3b3890` 使用两级 switch：选择字节表 `0x3b3d8c`，目标 RVA 表 `0x3b3d14`。索引 `0x22` 的选择字节为 21，落到 `0x3b3b2f`：

```text
3b3b2f  mov rax,[rbp+bb0]     ; 图形对象 RT 数量函数槽
3b3b36  mov rdx,rbp
        jmp 3b3cc2
3b3cc2  mov rcx,rsi           ; GPU 对象
        call [dffaa8]         ; CFG 间接调用，目标在 rax
3b3ccb  mov [rdi],eax         ; 填入该 index 的 data
```

相邻索引 `0x1d` 使用槽 `+0xBA8`，`0x23` 调用另一 helper。不能把所有对象上同样的 `+0xBB0` 偏移视为同一方法；扫描得到的其他类构造器、日志 stub 不是这条 RT 路径的证据。

证据：`internal-gr-exports.json`、`internal-static-gr-producer-disasm.txt`、`legacy-gr-cache-fill-disasm.txt`、`gr-info-switch-cases.json`、`gr-info-dispatcher-*-disasm.txt`。

## 后续：两个 RT 数量实现的条件

HAL 注册函数 `0x1788f60` 从输入对象 `+0x10` 取内部编号，并按分组、bit mask 选择函数。已核对的注册分支将 `0x7d3a50` 或 `0xa3a820` 写入目标对象 `+0xBB0`。当前没有把该内部编号映射到 GA102，故不能宣称 90HX 正在执行其中任何一条。

`0x7d3a50` 的行为可表述为：

```text
service = GPU->field_2100
status = service->method_120(GPU, service, 0xff00008d, &enabled)
if (status != 0 || enabled == 0) return 0
return helper_3b3770(GPU, Graphics)
```

`0xFF00008D` 是此处的查询标识，**不是已确认的 BAR 偏移或制造 fuse 地址**。服务类、方法名称和标识语义仍待独立核实。

`0xa3a820` 也存在查询 `0xFF00008D` 的分支；另一分支通过同一服务槽 `+0x108`，传入 `0x3814`，从输出取 bit9（`shr eax,9; and eax,1`）。分支由 GPU 对象两个字节字段决定。不能仅因 `0x3814` 与旧注释相似，就加上某个 base、认定为 `0x823814`，或把这一实现套到 GA102。

共同的 `0x3b3770` 数量 helper 调用图形对象槽 `+0x9F0`，再调用 `0x7e2b70`，相乘返回。两个因子的硬件含义尚未命名；这不是硬件性能测试。

证据：`rt-count-hal-selection-disasm.txt`、`rt-count-implementation-disasm.txt`、`rt-count-newer-implementation-disasm.txt`、`rt-count-calculation-disasm.txt`、`rt-implementations-xrefs.json`。所有条件按副本现有机器码记录；没有同版本原版对照，不能据此判断是否已经被修改。

## 下一阶段的具体目标

1. 将 `0x1788f60` 的内部编号与 GA102 对齐，确认实际 RT 实现。
2. 定位 GPU `+0x2100` 服务对象及槽 `+0x120/+0x108`；解出 `0xFF00008D` 的数据来源，区分制造值、有效状态、缓存和策略。
3. 取得 GA102 override 的准确字段定义，再研究它是否能改变这条路径消费的值及缓存更新时间。当前没有可靠写值。

离线内存指令筛选工具 `tools/DecodeRtRefs.cs` 使用 [Iced 1.21.0](https://github.com/icedland/iced)（MIT），依赖和编译产物仅保存在忽略的日志目录。需要支持 C# `in` 参数的现代编译器；已用本机 Visual Studio Roslyn 编译并扫描驱动副本。线性解码可能扫到嵌入数据或丢失对齐，因此候选必须从已知入口重新反汇编核对。上述关键 RT 实现均已从函数入口核对。

## 后续：GA102 分支和 `0xFF00008D` 输入已对齐

### Windows 自身的芯片编号证据

公开生成源码 [g_chips2halspec_nvoc.c](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/61dcc93722ecb418bb5f2e00923f05b4b8051dd1/src/nvidia/generated/g_chips2halspec_nvoc.c) 将 `arch=0x17, impl=2` 的 GA102 编号设为 43。不能单凭 Linux 编号套用 Windows，本轮还核对了副本自身：

```text
2a85c0  芯片编号初始化入口
2a8605  cmp edx,17h
2a8615  cmp r8d,2
2a861b  mov word ptr [rcx],2bh
```

调用者 `0x174bc90` 在 `0x174bca7` 将输出地址指向对象 `+0x10`，再调用 `0x2a85c0`。这与 HAL 注册读取的 word `+0x10` 对齐。结合已采集 BOOT0 的架构/实现字段，本轮可以静态确认 GA102 的选择条件，而非仅从公开版本推断。

将 `0x2B` 代入 RT 注册 `0x1788f60`：组为 1、组内编号 11，命中 mask `0x01F0F8E0`，槽 `+0xBB0` 指向 `0x7d3a50`。该路径查询 `0xFF00008D`；此前另一实现 `0xa3a820` 的 bit9 分支不应套用为 GA102 RT 数量判断。

证据：`windows-chip-index-disasm.txt`、`chip-index-field-init-disasm.txt`、`chip-index-init-xrefs.json`、`rt-count-hal-selection-disasm.txt`。

### 服务对象、查询映射和直接读取

`0x118ac0` 将 GPU 对象 `+0x2100` 的地址作为创建输出，指定类 ID `0x95BA71`。`.rdata` 的类定义 `0xed52e0` 指向创建入口 `0x17523c0`；入口跳到 `0x1753f20`，其中 `0x17541b1` 调用 HAL 注册 `0x17523d0`。未取得此类的正式名称，按已核对的类 ID 称呼。

编号 `0x2B` 的该服务注册如下：

| 槽 | 实现 RVA | 用途（根据机器码） |
| --- | --- | --- |
| `+0x108` | `0x31db90` | 原始偏移读取，含路由/拦截分支 |
| `+0x120` | `0x95a360` | 逻辑标识转换为偏移/移位/掩码 |
| `+0x168` | `0x95a0f0` | 直接读取分支的地址映射 |

在 `0x95a360` 中，输入 `0xFF00008D` 经 32 位加 `0x01000000` 归一化为 `0x8D`；两级 switch 的 selector 为 60，落到 RVA `0x95a677`。此 case 设置相对偏移 `0x168`，共同尾部设 mask=1、shift=0，调用槽 `+0x108` 后返回 `(value >> 0) & 1`。

`0x31db90` 的直接读取分支调用槽 `+0x168` 转换偏移，再传给既有映射内存读取 helper `0x1259a0`。`0x95a0f0` 对输入 `0x168` 的计算结果为 `0x820168`。因此，**这条 GA102 静态 RT 数量路径的直接 MMIO 输入为 `0x820168` bit0**；非零允许进入数量计算，为零返回零。

仍需保留以下边界：

- 原始偏移读取函数存在其他路由/拦截分支，运行时未动态跟踪，直接 BAR 采样不一定等于服务返回值。
- 尚未取得原始 GA102 头文件来给 `0x820168` 命名；此处不将它宣称为已确认的 `NV_FUSE_OPT_SM_TTU_EN` 定义。
- 未证明 `0x823808` override 会影响该输入，也未确认 bit24/25 的正式编码。
- 未证明制造 fuse、有效 READOUT、产品策略或固件之间的最终关系，更没有真实性能恢复证据。

证据：`service2100-create-disasm.txt`、`service-classid-scan.json`、`service-object-init-disasm.txt`、`service-hal-registration-complete-disasm.txt`、`ampere-query-switch-case.json`、`ampere-rt-query-case-disasm.txt`、`ampere-query-tail-disasm.txt`、`service-register-read-disasm.txt`、`service-address-map-disasm.txt`、`service-address-map-evaluated.json`。

### 新增可选只读采集

`tools/ReadOnlyRtProbe.ps1` 新增 `-ReadRtFuse`，必须同时指定 `-ReadMmio`。原有选项默认行为不变。新增参数仅额外读取一次 `BAR0+0x820168`，打印原值及 bit0；全 1 输出标为不可解释，不给出位状态。没有加入写 IOCTL、复位、驱动安装、地址自由输入或新 READOUT 猜测。

在管理员 PowerShell 采集：

```powershell
& 'E:\code\90HX\90HXWindowsUnlock\90HXWindowsUnlock\tools\ReadOnlyRtProbe.ps1' -ReadMmio -ReadRtFuse
```

已验证：x64 Framework 编译通过；无硬件访问的枚举自检通过；缺少 `--read-mmio` 时 `--read-rt-fuse` 在任何设备访问前报错；PowerShell 语法解析通过。当前工具进程没有管理员权限，尚未替用户采集新增地址，也未执行 RT 工作负载。

后续先取得该地址原值，再继续核实 override 与此输入的关联。即使 bit0=0 且 DXR Tier 1.0，也应先核对运行时路由和缓存，不能把差异直接视为软光追、驱动已 patch 或硬件故障。

## 实测更新：原始输入为零，NVAPI 也报告零 RT Core

用户在管理员 PowerShell 运行新增只读采样，UTC `2026-10-04T10:03:46.3014995Z`：

```text
PCI=10de:220d; BDF=02:00.0; BOOT0=0xb72000a1
BAR0+0x823808=0x00100283
BAR0+0x820168=0x00000000
R616_RT_QUERY_RAW_BIT0=0
RaytracingTier=10 (1.0)
ExitCode=0
```

原始日志 `logs/rt-probe-20261004-180346/probe.txt` 和 `pnp-driver.json` 已复核，版本仍为 `32.0.16.1656` / `oem14.inf`，GPU 身份相同。三个 speed selector 维持已有目标值。

为补上驱动报告数量，新增 `tools/ReadOnlyNvapi.cs`，经 `-ReadNvapi` 可选参数调用官方 NVAPI getters。使用 System32 的 `nvapi64.dll`，打印模块路径，以 PCI ID `0x220d10de` 选择唯一目标；`NV_GPU_INFO_V2` 按官方 80 字节布局清零 reserved 字段，版本为 `0x20050`。初始化、枚举、PCI 身份、GPU 信息查询和卸载均记录状态码，失败不把返回区解释为零。

接口和布局来源固定到 NVIDIA/nvapi commit `70d337db9186e968eab622f7e786de7e437faf3d`：

- [nvapi.h](https://github.com/NVIDIA/nvapi/blob/70d337db9186e968eab622f7e786de7e437faf3d/nvapi.h)：`NV_GPU_INFO_V2.rayTracingCores` 为支持的 RT Core 数量，`NvAPI_GPU_GetGPUInfo` 为公开查询接口。
- [nvapi_interface.h](https://github.com/NVIDIA/nvapi/blob/70d337db9186e968eab622f7e786de7e437faf3d/nvapi_interface.h)：QueryInterface IDs。

在当前非管理员工具进程中已执行公开查询，UTC `2026-10-04T10:06:57.3885941Z`：

```text
NVAPI_MODULE=C:\Windows\SYSTEM32\nvapi64.dll
NvAPI_GPU_GetPCIIdentifiers[0] Status=0
DeviceId=0x220d10de; Subsystem=0x155510de; Revision=0xa1
NvAPI_GPU_GetGPUInfo(V2) Status=0
NVAPI_RayTracingCores=0
NVAPI_TensorCores=200
RaytracingTier=10 (1.0)
NvAPI_Unload Status=0
ExitCode=0
```

完整记录：`logs/rt-probe-20261004-nvapi/probe.txt`，同时保存 stdout/stderr 和 PnP 身份。新增工具已编译并实际查询成功，既有无硬件枚举自检仍通过。没有执行 MMIO 写入、设备复位、私有 RM 控制或 RT 工作负载。

本轮三层证据是：原始 bit0=0、公开驱动数量=0、API 能力=DXR 1.0。它们说明当前数量报告并未恢复，且 DXR Tier 不能用作成功判据。尚未独立追踪此 NVAPI getter 到此前的 GR 信息表，因此不以数值一致代替调用链证明。

这也没有证明硬件 RT 单元缺失、正在执行软件光追、驱动必须 patch 或已经 patch。下一关键问题仍是：准确的 GA102 override 定义，以及它是否能改变该输入或驱动消费的有效状态；其次是初始化缓存的更新规则。默认 `-ReadMmio` 采集范围不变，`-ReadNvapi` 不需要 CMP90HXDma 管理员访问。

## 对“显卡本身是否还需写寄存器”的进一步核查

当前不能证明必须写硬件 override，也不能证明仅修改数量判断即可恢复 RT。需要分开回答：

| 问题 | 当前证据 | 尚缺证据 |
| --- | --- | --- |
| 驱动是否报告 RT Core | NVAPI 报告 0 | override 或其他改变后是否更新 |
| GA102 数量判断读哪里 | 静态直接分支读 `0x820168` bit0；实测 0 | 路由分支、缓存、override 对此输入的影响 |
| 硬件有效 RT 状态 | 尚未确认 | GA102 字段定义和 READOUT 对照 |
| RT 单元能否正确工作 | 尚未测试 | 确认字段后分层实验和吞吐验证 |

因此，不能以“DXR 1.0 已有”认定无需硬件操作，也不能以“制造输入为零”认定必须写某个 override。强制返回数量只能改变报告的候选路径，不能代替硬件状态与性能证据。旧 SM_TTU 注释仍为未独立核实的线索。

### 排除一个非常相似的写入候选：GA100 poison override

离线指令解码（含相对偏移立即数）找到 RVA `0x66ce60`：

- 从 RVA `0xd143c0` 的 ASCII 字符串 `RmGlobalPoisonOverride` 查询配置。
- 检查相对偏移 `0x3804` 的 bit4，以及一个内部权限/模式条件。
- 读取相对偏移 `0x3808` 原值，配置 1 时 OR bit25，配置 2 时 OR bit24/25，然后调用原始偏移写入 helper `0x31dcd0`。

单看地址和位号，这很像旧注释中的 SM_TTU override；但 HAL 注册 `0x1716d00` 在 RVA `0x1718475` 起明确只在编号 `0x2A`（GA100）选择 `0x66ce60`。GA102 编号 `0x2B` 命中 mask `0x03F8FBE0`，使用空实现 `0x6170`。因此这段代码**不能作为 GA102 光追字段或写值的证据**，也不能以 GA100 的 poison 字段反证 GA102 同位置的正式定义。

这里没有得出 `0x03000000` 是 GA102 光追写值；没有执行该函数、写入相同位号或设置该配置项。证据：`quadro-override-rmw-disasm.txt`、`poison-override-hal-registration-disasm.txt`、`rt-override-path-xrefs.json`、`service-register-write-disasm.txt`。

另一 helper `0x959830` 会依据条件取 `0x3814` bit9 或 `0x39C` bit0，但它只注册到 GA100 服务的槽 `+0x170`。它也不能被用作 GA102 SM_TTU 有效状态 getter。证据：`readout-bit9-helper-disasm.txt`、`service-hal-registration-complete-disasm.txt`。

NVIDIA 公开的 [GA100 dev_fuse.h](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/61dcc93722ecb418bb5f2e00923f05b4b8051dd1/src/common/inc/swref/published/ampere/ga100/dev_fuse.h) 仅提供该地址的部分 ECC 字段，未给出本次所需 GA102 SM_TTU override 定义。搜索到的第三方芯片采样表不作为 GA102 字段编码的正式依据。

### 补齐可只读采样的原始 READOUT

GA102 的服务 HAL 在槽 `+0x1B8` 注册 `0x959170`：编号 `0x2B` 不属于排除的组内编号 5..9。该函数使用已核对的原始读取槽 `+0x108` 读偏移 `0x3814`，取 bit29；GA102 地址映射函数 `0x95a0f0` 将 `0x3814` 映射为 `0x823814`。本轮确认的是 GA102 驱动确有读取该地址的静态路径，**未给 bit29 或 bit9 命名为光追字段**。

证据：`feature-readout-generic-getters-disasm.txt`、`feature-readout-getter-registration.json`、`ga102-readout-getter-selection-disasm.txt`、`service-address-map-disasm.txt`。

新增可选 `-ReadFeatureReadout`（必须同时有 `-ReadMmio`），仅读取一次 `0x823814` 原值，不解码为 RT 开/关。原默认采样行为不变。已编译、自检和缺少依赖选项的失败路径验证；尚未实际采样新地址。

```powershell
& 'E:\code\90HX\90HXWindowsUnlock\90HXWindowsUnlock\tools\ReadOnlyRtProbe.ps1' -ReadMmio -ReadRtFuse -ReadFeatureReadout -ReadNvapi
```

这次完整基线可以把 override、原始数量输入、READOUT 和 NVAPI 数量放在同一次采样中对照。取得原值之后仍需正式 GA102 定义或可靠的可重复实验来确认状态含义；不能把 bit9 数值本身当作已证实 RT 硬件状态。

## 完整采样结果：READOUT 原值 `0x33`

用户采样 UTC `2026-10-04T10:20:34.4962151Z`，`logs/rt-probe-20261004-182034/probe.txt` 已复核。相同 PCI ID、BDF、BOOT0 和 LUID 下：

| 项目 | 结果 |
| --- | --- |
| `0x823808` 原值 | `0x00100283` |
| `0x820168` 原值 / bit0 | `0x00000000` / 0 |
| `0x823814` 原值 | `0x00000033` |
| `0x823814` bit9（纯算术） | 0 |
| NVAPI RT / Tensor 数量 | 0 / 200，Status=0 |
| DXR Tier | 1.0 |
| 退出状态 | 0 |

`0x33` 置位的是 bit0、1、4、5；bit9 为零。若旧注释中的 GA102 SM_TTU 有效状态位置后续得到核实，这将支持当前硬件有效 RT 状态未开启的假设。但此处仍不直接给 bit9 命名，也不由此生成任何写值。

目前可作出的判断：现有 compute/gfx 解锁状态未伴随 RT 数量恢复；硬件关闭与驱动报告关闭的两道限制需要分别研究。即便未来绕过 RT 数量的返回零分支，也没有证据保证硬件能正确执行遍历。是否必须写硬件 override、是否有效、是否同时需要 NVIDIA 驱动改动，均尚不能只由这次只读基线最终决定。

### NVAPI 接口追踪的实际边界

已在 `nvapi64_impl.dll` 中按官方接口 ID `0xAFD1B02C` 找到包装入口 RVA `0xb3fe0`。它以该 ID 查内部映射，取出函数指针再间接调用；不是直接计算 RT 数量的代码。指针还从 `.data` RVA `0x502200` 被引用。此处尚未追到实际实现和 GR index `0x22` 的请求，不能将此前泛用 GR 查询片段直接命名为 GetGPUInfo 的消费路径。

证据：`nvapi-gpuinfo-id-candidates.tsv`、`nvapi-gpuinfo-wrapper-disasm.txt`、`nvapi-gpuinfo-wrapper-xrefs.json`。本轮仅读取日志和离线副本，没有新增 GPU 操作或调用该私有查表函数。

下一步源码目标仍是取得可独立核实的 GA102 字段定义，尤其 value/override 编码、有效状态如何接入硬件及复位/初始化时序。当前不再要求重复相同采样；新采样仅应在新的可靠证据或状态变化后进行。

## 继续核查：公开硬件文档与剩余 READOUT 候选

本轮固定 NVIDIA/open-gpu-doc 提交 `9fdf5c4062007929d9f4e6cbad9c9771fe61b880`，保存完整递归树（407 条，`truncated=false`）。其 [GA102 文档目录](https://github.com/NVIDIA/open-gpu-doc/tree/9fdf5c4062007929d9f4e6cbad9c9771fe61b880/manuals/ampere/ga102) 包含 CE、CTXSW、显示、音频、PCIe、TRIM、VM 共八份寄存器资料，没有 `dev_fuse` 文件。这里的结论仅是该固定提交没有所需 fuse 头文件，不能推及所有 NVIDIA 文档或内部源码。

旧包 `common/regs.h` 顶部明确声称来源为 `drivers/common/inc/hwref/ampere/ga102/dev_fuse.h`。但在本机 `E:\code\90HX`、`C:\Users\L\Downloads` 按文件名查找未取得原始文件；2026-10-04 访问其 README 指向的 `GreenDamTan/NVPermissive` GitHub API 返回 404（无法区分私有、移除或其他不可访问原因）。因此目前只有二次整理的注释，没有正式 value/override 枚举。

另核对 [Icyoung/cmpunlocker](https://github.com/Icyoung/cmpunlocker/blob/bb2ae3fe2d8f5e7d6574e2a15311f2589dda33bc/tools/gr_unlock/README.md) 固定提交 `bb2ae3fe2d8f5e7d6574e2a15311f2589dda33bc`。其说明明确列 RT unlock 为未实现；`fetch-core.sh` 仍取同一个 `380bdf3` 分发包，并非独立的 GA102 字段来源。不能把该项目的图形解锁成功当作光追成功记录。保存目录为 `logs/rt-research-20261004/public`，只读取了脚本文本，没有执行下载器、安装器或硬件流程。

离线补齐此前立即数扫描中的 READOUT 候选：

| 函数 RVA | 静态提取位/掩码 |
| --- | --- |
| `0x671fa0` | bit8 |
| `0x9f54b0` | bit30 |
| `0x9591d0` | bit27 |
| `0x959230` | bit31 |
| `0x959290` | bit17 |
| `0x9592f0` | 非零测试 `0x000c0000` |
| `0x959350` | bit13 |
| `0x9593b0` | 非零测试 `0x01804000` |
| `0x959410` | bit12 |
| `0x9594c0` | READOUT bit16，另读映射出的另一寄存器 bit15 |

这批函数没有补出 GA102 的 bit9 语义或 override 编码；其字段名称和各芯片 HAL 选择没有全部确定，故只保存位操作，不赋予 RT 含义。两处新候选分别由 HAL 初始化 RVA `0x1716d00`、`0x1777fff` 引用，并不是已核对 GA102 的 RT Core 数量入口。证据：`readout-extra-671fa0-disasm.txt`、`readout-extra-9f54b0-disasm.txt`、`readout-extra-xrefs.json`、`readout-remaining-getters-disasm.txt`。立即数扫描仍有线性解码、间接寻址和固件不可见等覆盖限制。

当前实验判断保持不变：尚未有充分依据回答“必须写寄存器”，也没有依据回答“无需写寄存器”。已核对的 GA102 数量直接分支取 `0x820168` bit0，而候选 override 在 `0x823808`；没有取得两者之间的硬件连动证据。即使 bit9 后续确认为有效 RT 状态，驱动仍可能读取不受 override 影响的输入，这正是可能需要同时修改 NVIDIA 驱动的原因，而不是已经证明必须修改。

下一步应优先取得上述原始 GA102 `dev_fuse.h`（带版本/来源及字段枚举），或可审计的 GA102 SM_TTU 初始化实现；正常 GA102 对照采样只能辅助解释，不能单独解决 override 编码。未增加任何 MMIO 写入、GPU 复位、驱动修改或性能测试。

## 用户补充分发目录核对

检查 `E:\code\90HX\NVPermissiveEFI-WindowsUnlock-v0.2.1\nvpermissive-dist-380bdf3\nvpermissive-dist-380bdf3`，共有 10 个文件。逐文件 SHA256 对照此前使用的 `_CMP90HX-WindowsUnlock\vendor\nvpermissive-dist-380bdf3`，10 个文件全部一致，包括 `common/regs.h`、Linux 薄层源码以及 830032 字节的 `obj/nvpermissive-core.o`。比较证据保存为 `logs/rt-research-20261004/supplement-380bdf3-comparison.json`。

该目录没有增加原始 GA102 `dev_fuse.h`、SM_TTU value/override 枚举或光追写入实现。README 与 Makefile 明确描述核心逻辑预编译在 `.o` 中，公开的 Linux 源文件是宿主薄层。因核心对象与此前分析副本完全一致，不把它作为独立证据，也无需重复相同反汇编。补充文件有助于确认包的完整性和来源关系，但未解决可靠光追写值问题。本轮仅检查文件和保存比较结果。

## 从计算/图形写入模式推导条件候选（不可作为写入依据）

用户要求核对现有写入并推算可能的光追编码。`common/regs.h` 的 `g_compute_regs` 明确列出 `SS1=8`、`SS0=0x88888888`；`g_graphics_regs` 列出 `GFX_SPD=4`。三个地址分别为 `0x823820`、`0x82381c`、`0x823830`，与实际解锁后只读结果一致。它们是速度选择 override，不能推及功能启用字段的 polarity。图形字段在本包注释/宏中为 value[1:0]、override[2]，4 的位形态为 override=1、value=0；SS0 的各 nibble 都为 8，可提出“最高位作 override、低位作速度编码”的假设，但未取得原始 GA102 SS 字段枚举，不把该假设当正式定义，也不把分组数当 SM 数。

仅在旧 SM_TTU 位号（value=24、override=25）正确，且 override=1 表示覆盖生效的前提下，两个 polarity 分支的算术候选为：

| 额外假设 | 字段掩码（条件推导） | 字段编码（条件推导） | 对原值 `0x00100283` 保留其他字段后的条件结果 |
| --- | --- | --- | --- |
| enabled=0 | `0x03000000` | `0x02000000` | `0x02100283` |
| enabled=1 | `0x03000000` | `0x03000000` | `0x03100283` |

这些数字只枚举假设，并不是已确认、安全或有效的实验写值；掩码本身也依赖尚未核实的位号。不能用速度全速编码 0 推导 RT enabled 必为 0，反之也不能因名称含 EN 就推导 override value 必为 1。尚无权限、初始化/复位、制造 fuse 优先级及有效状态连动证据。不提供执行写入脚本、不改现有流程、不执行试写。即使后续确认编码，也必须 RMW 保留其他字段，并独立验证硬件有效状态和驱动数量路径。

## 时序证据的获取方法与现有序列

核对当前 Windows 源码：`ui/NativeWorkflow.cs` 在运行 full-unlock 前停用原 PnP 设备并等待停用；`src/FullUnlock.cs` 的 `Run` 进行准备、初始双 SBR、compute、graphics、Gen2 权限与链路步骤，验证后由 UI 重新启用原设备。managed 核心的 compute/graphics 通过 `do_permissive_with_handoff` 内部调用宿主 handoff 执行双 SBR；旧核心分支由宿主在 `do_permissive` 返回后执行双 SBR。两条分支的调用边界不同，不能把 managed 的内部写入与 reset 相对顺序仅从外层调用推定出来。

`EfiResetSequence.Once` 保留 SBR 断言 100 ms，解除后快速路径至少等 100 ms 并轮询配置/链路返回；随后恢复 BAR/命令并等待 bootstrap 状态。保守路径使用较长固定等待，快速路径有配置/链路与 readiness 超时。它们是现有 GPU/桥恢复的等待策略，不是 SM_TTU 的 latch 或重新消费规范。未执行 reset 来取得本轮证据。

RT 时序应从三类证据共同取得：原始寄存器/复位域说明（何时锁存、何种 reset 保留/清除）、GA102 专属驱动/固件初始化代码（何时读 fuse/READOUT、何时填充能力缓存）、阶段观测（在下一次已授权的既有解锁中增加只读采样，分别记录权限开放、字段写入、reset、驱动重新启用前后）。最后一项可先用已经确认的计算/图形字段验证采样与序列；RT 寄存器的未改动观测只提供基线，不能独自证明修改后的消费时机。

候选插入窗口是对应权限已开放、NVIDIA 驱动重新接管前，但其在双 SBR 前后的位置仍待上述证据确认。字段定义确定之后可用每次从同一基线开始、单次只改变一个变量的实验确认剩余时序问题；不要求所有未知都先由文档解决，也不以任意延时或连续试值代替可判断的实验。本轮未修改主流程或增加硬件操作。
