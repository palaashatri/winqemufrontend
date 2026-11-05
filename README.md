<p align="center">
  <img src="assets/brand/wordmark.png" alt="WinQEMU — Virtual Machine Manager for Windows 11" width="540" />
</p>

<h1 align="center">WinQEMU — Virtual Machine Manager for Windows 11</h1>

<p align="center">
  <strong>GNOME Boxes simplicity + UTM polish + virt-manager power — built natively for Windows 11 with C#, .NET 10, Windows App SDK, and WinUI 3.</strong>
</p>

---

## Overview

**WinQEMU** (`winqemufrontend`) is a Windows-native consumer and power-user virtual machine manager for stock **QEMU**. Users can create, install, run, pause, reset, snapshot, clone, export, and inspect virtual machines across **`x86_64`**, **`i386`**, **`aarch64` (ARM64)**, **`riscv64` (RISC-V 64)**, and **`ppc64` (PowerPC 64)** without opening a terminal or memorizing QEMU command-line flags.

### Key Capabilities

- **Windows 11 Fluent Design (WinUI 3)**: Native Mica/Acrylic backdrop, integrated title bar with live WHPX & QEMU runtime status pills, Dark & Light themes, responsive Wide & Snapped (520 px) layouts, full keyboard navigation (`Ctrl+N`, `Alt+1`, `Alt+2`, `F11`), and UI Automation accessibility metadata.
- **Hardware Acceleration (WHPX) & Multi-Arch Emulation (TCG)**:
  - Native `WinHvPlatform.dll` P/Invoke + live `qemu-system-x86_64 -accel whpx` smoke verification.
  - Automatic fallback to TCG for cross-architecture guests (`aarch64`, `riscv64`, `ppc64`, `i386`) with honest live per-VM accelerator badges (`WHPX` vs `TCG`).
  - One-click UAC-elevated helper (`dism.exe /Online /Enable-Feature /FeatureName:Microsoft-HypervisorPlatform`) when WHPX is disabled.
- **Express "New VM from ISO" Wizard & Unattended Install**:
  - Reads ISO 9660 Primary Volume Descriptors (Sector 16) + filename signatures to auto-classify Windows 11/10, Ubuntu, Fedora, Debian, Arch, Linux Mint, Alpine, FreeBSD, OpenBSD, and macOS/Darwin across architectures.
  - Generates Cloud-Init (`CIDATA` `user-data`/`meta-data`) and Windows `Autounattend.xml` (`OEMDRV`) seed ISOs in pure C# (`Iso9660Builder`) without external tools.
- **Embedded Guest Display + Detachable Multi-Window Console**:
  - Pure-C# RFB 3.8 localhost VNC client (`127.0.0.1:<port>`) + unsafe dirty-rectangle BGRA blitter (`FastFramebufferPresenter`), aspect-ratio scaling, keyboard X11 keysym mapping, mouse pointer coordinate scaling, clipboard sync, and zero-dependency PNG screenshot capture.
- **Event-Driven QMP Control Plane & Developer Drawer**:
  - Full asynchronous JSON QMP client over loopback TCP with greeting/capabilities handshake, request ID correlation, out-of-order response matching, live `STOP`/`RESUME`/`RESET`/`POWERDOWN`/`SHUTDOWN` events, interactive QMP Protocol Inspector (with destructive command guard), and live `COM1`/`ttyAMA0` Serial Console.
- **QCOW2 Snapshots, Full & Linked Clones, and `.qvm` Portable Packages**:
  - Offline (`qemu-img snapshot`) and live (`savevm`/`loadvm`/`delvm`) snapshots, full independent clones, fast backing-file linked clones with dependency-graph deletion protection, and Zip-Slip-hardened `.qvm` export/import.

---

## Visual Proof — Application UI & Multi-Architecture Boot Gallery

### 1. Application Views (Dark, Light, Snapped, Wizard, Hardware Editor, Snapshots, Diagnostics)

| Dark Mode Library (`1380x880`) | Light Mode Library (`1380x880`) |
|---|---|
| ![Home Library Dark](docs/qa-screenshots/01-library-dark-wide.png) | ![Home Library Light](docs/qa-screenshots/02-library-light-wide.png) |

| Windows 11 Snapped Narrow Layout (`520x840`) | Live Thumbnails After Multi-Arch Verification |
|---|---|
| ![Snapped Narrow Layout](docs/qa-screenshots/03-library-snapped-narrow.png) | ![Library With Live Thumbnails](docs/qa-screenshots/18-library-after-multiarch-verification.png) |

| Express "New VM" Wizard | Advanced Hardware Editor & Generated Command |
|---|---|
| ![Express New VM Wizard](docs/qa-screenshots/04-express-new-vm-wizard.png) | ![Advanced Hardware Editor](docs/qa-screenshots/05-advanced-hardware-editor.png) |

| QCOW2 Snapshots & Linked/Full Cloning | Settings, WHPX Status & 100/100 Readiness Report |
|---|---|
| ![Snapshots and Cloning](docs/qa-screenshots/06-snapshots-and-cloning.png) | ![Settings and Diagnostics](docs/qa-screenshots/08-settings-and-diagnostics.png) |

---

### 2. Bootable Linux, FreeBSD, Windows & macOS VMs Across All Supported Architectures

Every screenshot below was captured from a live `qemu-system-<arch>` process running inside WinQEMU's embedded VNC display with the live Serial Console (`COM1`/`UART`) and QMP Protocol Inspector (`query-status` -> `"running": true`) open alongside the guest framebuffer:

#### Linux Across `x86_64` (WHPX), `aarch64` (ARM64 UEFI), `riscv64` (RISC-V 64 OpenSBI+UEFI), and `i386` (32-bit PC)

| Ubuntu 24.04 LTS (`x86_64` — WHPX) | Alpine Linux 3.21 (`aarch64` / ARM64 — EDK2 UEFI GOP) |
|---|---|
| ![Ubuntu 24.04 x86_64](docs/qa-screenshots/09-app-window-ubuntu-linux-24_04-lts-(x86_64).png) | ![Alpine Linux ARM64](docs/qa-screenshots/10-app-window-alpine-linux-3_21-(arm64-_-aarch64).png) |

| Debian GNU/Linux 12 (`riscv64` / RISC-V 64 — OpenSBI + UEFI GOP) | Debian GNU/Linux Legacy (`i386` / 32-bit x86 — SeaBIOS) |
|---|---|
| ![Debian RISC-V 64](docs/qa-screenshots/11-app-window-debian-gnu_linux-12-(risc-v-64-_-riscv64).png) | ![Linux i386](docs/qa-screenshots/12-app-window-linux-32-bit-legacy-edition-(i386).png) |

#### FreeBSD Across `x86_64` (WHPX) and `aarch64` (ARM64 UEFI)

| FreeBSD 14.2-RELEASE (`x86_64` / amd64 — WHPX) | FreeBSD 14.2-RELEASE (`aarch64` / arm64 — EDK2 UEFI GOP) |
|---|---|
| ![FreeBSD 14.2 x86_64](docs/qa-screenshots/13-app-window-freebsd-14_2-release-(x86_64).png) | ![FreeBSD 14.2 ARM64](docs/qa-screenshots/14-app-window-freebsd-14_2-release-(arm64-_-aarch64).png) |

#### Windows & macOS / Darwin Across `x86_64` (WHPX) and `ppc64` (PowerPC 64 `mac99` OpenBIOS)

| Windows 11 Pro 24H2 (`x86_64` — WHPX) | macOS / Darwin 24.3 (`x86_64` — Q35 + ICH9 AHCI + WHPX) |
|---|---|
| ![Windows 11 x86_64](docs/qa-screenshots/15-app-window-windows-11-pro-24h2-(x86_64).png) | ![macOS Darwin x86_64](docs/qa-screenshots/16-app-window-macos-darwin-24_3-(x86_64).png) |

| Mac OS X / Darwin PowerPC 64 (`ppc64` — PowerMac G5 `970fx` `mac99` OpenBIOS) | Multi-Architecture Lab & Official OS Catalog |
|---|---|
| ![Mac OS X PowerPC 64](docs/qa-screenshots/17-app-window-mac-os-x-_-darwin-powerpc-(ppc64-mac99).png) | ![Multi-Arch Lab](docs/qa-screenshots/07-multiarch-lab-and-downloads.png) |

---

## Build, Test & Run Commands

```powershell
# Restore & build the full solution
dotnet build WinQemu.sln

# Run all 4 automated test suites (Unit, FakeQmp, QemuIntegration, UiAutomation)
dotnet test WinQemu.sln

# Launch the WinUI 3 application
dotnet run --project src/App/WinQemu.App.csproj

# Run the automated visual QA & multi-architecture VM boot verification tour
dotnet run --project src/App/WinQemu.App.csproj -- --qa-tour docs/qa-screenshots
```

## Documentation & Specifications

- [Product Specification & Acceptance Tests](docs/PRODUCT_SPEC.md)
- [Architecture Overview](docs/ARCHITECTURE.md)
- [Security Model & Threat Boundaries](docs/SECURITY.md)
- [Third-Party Notices & GPLv2 Process Separation](THIRD_PARTY_NOTICES.md)
- [Agent & Contributor Guidelines](AGENTS.md)
