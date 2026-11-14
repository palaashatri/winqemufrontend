# Third-Party Notices & Licensing Compliance (§67, §80)

WinQEMU (`winqemufrontend`) is an independent Windows-native frontend application written in C# on .NET 10 and Windows App SDK / WinUI 3.

## 1. Strict Process Separation from QEMU (GPLv2)

- **WinQEMU never links against QEMU libraries, never copies QEMU source code, and never compiles QEMU code into its own binaries (§6, §67).**
- Stock `qemu-system-*` and `qemu-img` binaries execute strictly as separate user-mode processes launched via `System.Diagnostics.Process` and controlled over local loopback JSON QMP (`127.0.0.1`) and RFB 3.8 sockets.
- When the user downloads the managed QEMU for Windows runtime via `QemuRuntimeInstaller`, all upstream license files (`COPYING`, `COPYING.LIB`, `edk2-licenses.txt`) included in the official QEMU distribution are preserved intact in `%LOCALAPPDATA%\WinQemu\Runtime\qemu`.
- Official QEMU source code is available at https://www.qemu.org/download/#source and Windows build recipes at https://qemu.weilnetz.de/src/.

---

## 2. Firmware & Optional Runtime Components

| Component | License | Role & Provenance |
|---|---|---|
| **QEMU (`qemu-system-*`, `qemu-img`)** | GPLv2 / LGPLv2.1 | Separate process hypervisor / emulator runtime (`https://www.qemu.org`) |
| **EDK2 / OVMF (`edk2-*-code.fd`, `edk2-*-vars.fd`)** | BSD-2-Clause-Patent | UEFI firmware images bundled in QEMU `share/` (`share/edk2-licenses.txt`) |
| **SeaBIOS (`bios-256k.bin`)** | LGPLv3 | x86 legacy BIOS bundled in QEMU `share/` |
| **OpenBIOS (`openbios-ppc`, `openbios-sparc64`)** | GPLv2 | IEEE 1275 Open Firmware for PowerPC `mac99` and SPARC64 |
| **OpenSBI (`opensbi-riscv64-generic-fw_dynamic.bin`)** | BSD-2-Clause | RISC-V Supervisor Binary Interface firmware bundled in QEMU `share/` |

---

## 3. NuGet Dependencies (§80)

WinQEMU minimizes external dependencies:

| Package | License | Used By | Justification |
|---|---|---|---|
| `Microsoft.WindowsAppSDK` (`1.6.250205002`) | MIT / Microsoft Windows App SDK License | `WinQemu.App` | Official WinUI 3 native Windows 11 UI framework (§2) |
| `Microsoft.Windows.SDK.BuildTools` (`10.0.26100.1742`) | Microsoft SDK License | `WinQemu.App` | XAML compiler and PRI resource packaging |
| `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio` | MIT / Apache-2.0 | `tests/*` | Unit, fake QMP, real-QEMU integration, and UI automation test runners |

All core libraries (`WinQemu.Core`, `WinQemu.Qemu`, `WinQemu.Storage`, `WinQemu.Networking`, `WinQemu.Firmware`, `WinQemu.Security`, `WinQemu.Integration`, `WinQemu.Display`, `WinQemu.Native.GuestDisplay`) have **zero third-party NuGet dependencies**.
