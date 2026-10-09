# Console 使用独立编译的后端包

Native、Session、Console 可以分别更新版本。先在各自项目中编译、签名后端，再把配套成品放到 Console 下的固定输入目录。Console 构建只读取这些文件，不查找相邻源码仓库，不编译后端，也不修改原项目的输出。

```text
90HXWinUnlockConsole/
  backends/
    native/
      CMP90HXUnlocker.exe
      CMP90HXUnlocker.exe.config
      CMP90HXDma.sys
      nvpermissive-core.o
    session/
      CMP90HXUnlocker.exe
      CMP90HXUnlocker.exe.config  （如果生成了该文件）
      CMP90HXUnlockSession.sys
      CMP90HXUnlockSession.inf
      CMP90HXUnlockSession.cat
```

必须复制完整配套包，不能只替换 SYS：驱动含有对应 worker 的 SHA256 校验。若签名 worker，必须先签 EXE，再生成调用者策略、编译并签 SYS；Session 在签 SYS 后重新生成并签 CAT。Native 核心对象也应来自该 worker 对应的构建。

Native 普通构建的 worker/config 在其 build 目录、SYS 在 build/dma-driver，核心在其选定核心输出位置。Session 成品在其 build/filter-driver。若使用 Native 源码核心构建，复制该构建生成的配套 worker/SYS/核心；在 Console 输入目录将所选核心统一命名为 nvpermissive-core.o。不同后端的同名 EXE 必须留在各自目录中。

## Console 构建

```powershell
cd E:\code\90HX\90HXWindowsUnlock\90HXWinUnlockConsole
.\build.ps1
```

构建检查两个包的必需文件、每个 SYS 内的 worker 哈希，计算本地包文件哈希及独立版本，生成 build/BackendPackagePolicy.cs。然后仅编译前后台，将运行文件复制到固定 build 目录的 runtime、driver、core 和 session 子目录，并生成 files.sha256.json 和 compiled-backends.json。

缺文件或 SYS/worker 混用会直接报错，不会回退到同级项目，不会自动修复或编译后端。普通构建允许尚未签名的包用于编译验证；实际加载仍检查驱动签名，build-release.ps1 要求两个后端的 SYS 及 Session CAT 签名有效。构建不会安装或加载驱动。

后端 CLI/协议必须与当前 Console 调用兼容；独立版本号不表示任意旧版本都兼容。文件哈希校验确认配套与完整性，不能代替硬件稳定性验证。

共用读取驱动属于 Console，仍使用其独立 build-reader-driver.ps1 构建及签名步骤。见 [共用读取驱动](shared-reader.md)。

## 更新与发布

更新任意后端时，完成后端构建和签名，完整替换对应 backends 子目录，再编译 Console；另一个后端无需重编。替换本地输入不会影响已经运行的后台，也不会立即切换正在加载的驱动。更新已加载 SYS 仍按原驱动要求重启。

```powershell
# 从已经准备、签名的输入生成正式发布包；不编译 Native/Session。
.\build-release.ps1 -NonInteractive
# 只打包已编译 Console；后端输入变化后必须先重新编译。
.\build-release.ps1 -PackageOnly
```

若旧后台正在使用 build/CMP90HXBackground.exe，先在管理员终端通过 --request Shutdown 正常退出并关闭前台，再编译。不要强制终止正在解锁的进程。

二进制输入在 .gitignore 中排除，backends 目录中的 README 保留在源码仓库。运行时仅使用随 Console 发布的本地文件，不再依赖 Native、Session 源码目录存在。
