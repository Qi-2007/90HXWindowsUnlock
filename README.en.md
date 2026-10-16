![Project Icon](ui/icon.ico)
# CMP 90HX Windows Unlock

[简体中文](README.md) | **English**

**Manage CMP 90HX unlocking, status monitoring, and idle power saving on Windows.**

![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4?style=flat-square) ![.NET Framework](https://img.shields.io/badge/.NET_Framework-4.8.1-512BD4?style=flat-square) [![GPL v3](https://img.shields.io/badge/License-GPL_v3-blue?style=flat-square)](LICENSE)

[Download](https://github.com/Qi-2007/90HXWindowsUnlock/releases) · [Quick Start](#quick-start) · [FAQ](#faq) · [Development and Build](#development-and-build) · [Report an Issue](https://github.com/Qi-2007/90HXWindowsUnlock/issues)

If this project has been useful to you, please give it a **Star** to support further updates.

CMP 90HX Windows Unlock provides a graphical console for unlocking NVIDIA CMP 90HX cards on Windows. It integrates Native and Session unlock methods, unlock status monitoring, and background power management. The frontend handles user interaction, while the backend handles hardware operations. After the window closes, the backend can continue power-saving and wake-up tasks according to your settings.

This directory contains the standalone **Console project**. The Native and Session backends are built separately and integrated into the console as complete, matching packages.

## Features

| Feature | Description |
| --- | --- |
| **Two unlock methods** | Select Native or Session in Settings and perform manual unlocking from the same console. |
| **GPU unlocking** | Unlock GPU features and PCIe Gen2 through drivers on Windows, without booting through EFI. |
| **Status monitoring** | View PCIe link status and compute, graphics, and ray-tracing override states. |
| **Driver management** | Install, update, and uninstall the status reader driver and the selected unlock driver, and view NVIDIA driver takeover status. |
| **Background automation** | Configure backend startup at boot, automatic unlocking at startup, and automatic unlocking after waking from sleep. |
| **Idle power saving** | Limit the GPU to P8 while idle, then restore automatic performance based on GPU load, video load, and application exceptions. |
| **Interface theme** | Customize the background image and the opacity of windows and log panels. |
| **Optional root certificate support** | When enabled at build time, check the root certificate before installing drivers and automatically install the bundled certificate if it is missing. |

> Unlock status only indicates that the source override registers have reached their target values. Actual functionality depends on the matching NVIDIA driver and initialization state. Make sure you install a specially patched driver that supports the corresponding features.

## Screenshot

![CMP 90HX Windows Unlock console preview](docs/images/console-preview.png)

## Download

Releases: [GitHub Releases](https://github.com/Qi-2007/90HXWindowsUnlock/releases).

Download and fully extract the runtime package before use. GitHub's Source code ZIP contains the source code and cannot be run directly as a compiled application.

> Keep the `native`, `session`, and `reader` directories, along with supporting files such as `files.sha256.json`. Program files are checked against matching hashes; missing or modified files may prevent the application from running.

## Quick Start

### Requirements

- Windows 10 / 11 x64 with support for [.NET Framework 4.8.1](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481).
- Unlocking currently targets only NVIDIA CMP 90HX cards (PCI ID `10DE:220D`). Install a third-party modified NVIDIA driver beforehand.
- Run the console with administrator privileges.

### First-Time Setup

The current interface is in Chinese; the button and option names below are translated for reference.

1. Fully extract the release package, run `CMP90HXControl.exe`, and approve the administrator permission request.
2. Click **Environment Check** to check the system's Kernel DMA Protection status.
3. If necessary, follow the result from the previous step and select the corresponding unlock method in **Settings** at the top right. Then open **Driver Management** and install the reader driver and the driver for the selected unlock method.
4. Click **Start Manual Unlock**, wait for completion, and review the operation log and post-unlock verification results.
5. After confirming that manual unlocking works, configure **Idle Power Saving**, **Start Backend at Boot**, automatic unlocking after wake-up, and other options as needed.

> Versions signed using a custom root certificate automatically install the bundled root certificate during driver installation if it is missing. Driver installation stops if certificate installation fails.

### Choosing an Unlock Method

| Method | How it works | Requirements |
| --- | --- | --- |
| **Native** | Performs the unlock transaction using a standalone worker and the Native driver. | Requires Kernel DMA Protection to be disabled. Does not switch the GPU driver and unlocks faster. |
| **Session** | Temporarily takes over the GPU using the Session driver, performs the unlock, and restores the original NVIDIA driver after confirming that DMA resources have been released. | Supports Kernel DMA Protection. |

When Kernel DMA Protection is enabled, the console blocks Native mode. Switch to Session mode manually. Changing methods uninstalls the old method's unlock driver; you must then install the new method's unlock driver in Driver Management.

> The GPU is temporarily disabled during unlocking, which may cause some applications to crash or behave unexpectedly. If the application reports an unconfirmed DMA state or requests a cold boot, follow the recovery instructions before trying again. Updating or uninstalling a loaded driver may also require a restart.

## FAQ

### Why are administrator privileges required?

Driver management, device enable/disable operations, and hardware access require administrator or SYSTEM privileges. The frontend communicates with the backend through protected shared memory, and the backend executes hardware operations sequentially.

### Why does driver installation fail?

Check whether the release you downloaded is signed using a custom root certificate. This version cannot load its drivers with Secure Boot enabled. Disable the relevant option in your BIOS, or use a version signed by a third party.

**Check whether BitLocker is enabled before changing Secure Boot settings. Changing these settings may invalidate BitLocker protection, lock you out of your drive, or cause other serious problems. You are responsible for any resulting consequences.**

Third parties may charge for signing. This is unrelated to the project author; evaluate and resolve such matters yourself. Feedback concerning third-party signing services is not accepted.

Also check Kernel DMA Protection. With it enabled, Native mode may have unpredictable effects, so the application refuses to load the Native driver. Disable the relevant BIOS option or use Session mode.

### Will power saving and automatic unlocking continue after I close the console?

Yes, while the backend is running, it continues to follow your settings. Closing the frontend does not stop the backend or cancel an unlock task that has already been submitted. To start the backend automatically after a system restart, enable **Start Backend at Boot** in Settings.

### Why is unlock status not refreshed in real time?

PCIe and unlock register states are collected manually through **Refresh Unlock Status**, with an additional verification after unlocking. If reading fails, the interface reports that the state cannot be confirmed rather than reusing an old snapshot. GPU load and power consumption in the power-saving window are sampled separately by the backend.

### Why install a custom root certificate?

A custom root certificate can be used to forge signing timestamps, allowing expired certificates to be used for driver signing. With Secure Boot enabled, Windows refuses to load such drivers, so this version requires Secure Boot to be disabled in the BIOS. If you need unlocking with Secure Boot enabled, sign the SYS files with valid certificates and rebuild the console yourself, or use a version signed by a third party.

### Can I copy only the EXE or replace just one SYS file?

Use the complete runtime package. Drivers include SHA256 checks for their corresponding workers, and the console also verifies the supporting files. See the [release instructions](docs/RELEASING_ZH.md) for the required update, signing, and packaging order.

### Ray tracing is shown as unlocked. Why does an application still not support it?

The displayed state comes from the Quadro source override. Actual functionality also depends on the NVIDIA driver. Confirm that the matching driver supports it, and test correctness and performance with an actual ray-tracing workload.

## Development and Build

If you would like to join us and contribute to unlocking more features or improving the application, contact me at [lll20001101@gmail.com](mailto:lll20001101@gmail.com). I can provide some assistance and resources.

The console uses WPF and .NET Framework 4.8.1. Install Visual Studio MSBuild and the corresponding Developer Pack. Rebuilding the Reader or unlock drivers also requires a matching Windows SDK / WDK.

Build and sign the Native and Session backends separately, then place the complete packages in `backends/native` and `backends/session`. The console build automatically compiles the Reader if it is missing, or asks whether to rebuild an existing Reader. After compilation, sign the SYS as prompted; the script validates it and updates the final hashes before compiling the frontend and backend. Use `-NonInteractive` to reuse an existing Reader or `-RebuildReader` to force a rebuild.

Run the following from this project's root directory:

```powershell
# Default build
.\build.ps1

# Include root certificate management and automatic installation before driver setup
.\build.ps1 -EnableRootCertificates

# Launch the built console
.\build\CMP90HXControl.exe
```

| Document | Contents |
| --- | --- |
| [Standalone backend packages](docs/backend-packages.md) | Native / Session package contents, matching files, and integration. |
| [Release instructions](docs/RELEASING_ZH.md) | Reader signing, build options, hash updates, and release packaging. |
| [Background controller](docs/background-control.md) | Automatic unlocking, startup tasks, configuration, and logs. |
| [Shared reader driver](docs/shared-reader.md) | Status reader responsibilities, interfaces, and validation. |

The frontend executable is `CMP90HXControl.exe`, and the backend executable is `CMP90HXBackground.exe`. Configuration is stored at `C:\ProgramData\CMP90HX\settings.json`.

The linked technical documents are currently in Chinese.

## Acknowledgments

- [GreenDamTan / RainCandyTech](https://github.com/GreenDamTan): NVPermissive core and related upstream work.
- [WildFlash1st / cmp90hx-unlock-for-windows](https://github.com/WildFlash1st/cmp90hx-unlock-for-windows): Related Windows unlocking work.
- [Qi](https://github.com/Qi-2007): Windows drivers, console, and backend integration.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party inputs and their sources.

## License

The project code uses [GNU GPL v3](LICENSE). Closed-source derivative development and all commercial use are prohibited. See the [third-party notices](THIRD_PARTY_NOTICES.md) for the sources and licensing scope of third-party core, firmware, and driver inputs.

## Feedback

Please submit a [GitHub Issue](https://github.com/Qi-2007/90HXWindowsUnlock/issues) and include:

- Console version, Native / Session method, and NVIDIA driver version.
- Windows version, Kernel DMA Protection status, and device problem code.
- Steps to reproduce, the complete error message, and the current operation's logs obtained through **Copy Logs** or **Open Directory**.

Before sharing logs, check local paths and device information, and remove anything you do not want to make public.
