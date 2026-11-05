# WinQEMU Architecture (§78, §79, §81)

## 1. Layered Module Graph

WinQEMU enforces a strict acyclic project graph where `WinQemu.Core` and `WinQemu.Qemu` have zero UI/XAML dependencies and are 100% unit-testable headless:

```text
WinQemu.App (WinUI 3 / Windows App SDK Views & ViewModels)
 ├── WinQemu.Integration        (OS catalog, multi-arch boot media factory, USB host discovery)
 ├── WinQemu.Storage            (QCOW2 SnapshotManager, Full/Linked CloneManager, ISO 9660 El Torito builder)
 ├── WinQemu.Firmware           (OVMF/EDK2 per-VM writable vars.fd manager)
 ├── WinQemu.Security           (SWTPM control channel & local-only endpoint validator)
 ├── WinQemu.Networking         (User-mode NAT & port forwarding conflict validator)
 ├── WinQemu.Native.GuestDisplay(Unsafe dirty-rectangle BGRA blitter, aspect scaler, zero-dep PNG encoder)
 ├── WinQemu.Display            (RFB 3.8 localhost VNC client, keysym mapper, pointer & clipboard bridge)
 ├── WinQemu.Qemu               (Runtime locator/installer, capability prober, WHPX detector, QemuCommandBuilder, QmpClient, QgaClient, QemuImgClient, QemuProcessManager)
 └── WinQemu.Core               (Schema v2 domain models, atomic JSON persistence, SchemaMigrator, GuestPresetCatalog, IsoClassifier, VmStateMachine, VmLockManager, LogRotator, DiagnosticsReportBuilder)
```

## 2. Process Supervision & QMP Control Plane (§6, §9, §40)

1. **Out-of-Process Execution**: Every VM runs in an isolated `qemu-system-<arch>.exe` process managed by `QemuProcessManager`.
2. **File Lock Protection**: Before launching QEMU or mutating disks, `VmLockManager` acquires an exclusive `.lock` file inside the VM directory (`<vm>/vm.lock`).
3. **QMP State Synchronization**: `QmpClient` connects to `-qmp tcp:127.0.0.1:<port>,server=on,wait=off`, negotiates `qmp_capabilities`, and subscribes to asynchronous lifecycle events (`STOP`, `RESUME`, `RESET`, `POWERDOWN`, `SHUTDOWN`, `GUEST_PANICKED`) to drive `VmStateMachine`.
4. **Embedded Framebuffer Streaming**: `RfbVncClient` connects to `-vnc 127.0.0.1:<display>` using RFB 3.8, requests 32bpp BGRA updates, blits dirty rectangles via `FastFramebufferPresenter` into a WinUI 3 `WriteableBitmap`, and forwards keyboard (`X11` keysyms), mouse, and clipboard events.
