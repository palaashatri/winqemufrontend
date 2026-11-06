# WinQEMU Security Model & Threat Boundaries (§56–§60, §81)

## 1. Local-Only Control & Display Transport (§10, §56)

- **QMP**: Bound strictly to `127.0.0.1:<ephemeral-port>`. `QemuCommandBuilder` and `SecurityValidator` reject `0.0.0.0` or LAN exposure.
- **VNC Embedded Display**: Bound strictly to `127.0.0.1:<display>`.
- **Serial Console & Guest Agent**: Bound strictly to `127.0.0.1`.
- **Port Forwarding**: User-mode NAT (`-netdev user`) defaults all `hostfwd` bindings to `127.0.0.1`.

## 2. Principle of Least Privilege (§57)

- `WinQemu.App.exe` runs **unelevated** (`asInvoker` in `src/App/app.manifest`).
- Enabling Windows Hypervisor Platform (`HypervisorPlatform`) is isolated to an explicit user-triggered `dism.exe /Online /Enable-Feature /FeatureName:HypervisorPlatform /All /NoRestart` UAC elevation request (`WhpxDetector.CreateElevatedEnableWhpxProcessStartInfo()`).

## 3. Portable Package (`.qvm`) & Input Sanitization (§58, §59)

- **Zip-Slip Prevention**: `VmConfigurationStore.ImportPortablePackageAsync` validates canonical paths (`Path.GetFullPath`) for every archive entry before extraction, throwing `InvalidDataException` on any `..` traversal attempt.
- **Argument List Construction**: `QemuCommandBuilder` populates `ProcessStartInfo.ArgumentList` directly (never `cmd.exe /c` shell concatenation) and escapes QEMU commas (`EscapeQemuCommaValue`).
- **Credential Hygiene**: Transient unattended install passwords (`Autounattend.xml` / `cloud-init`) are never persisted in `vm.json` and are automatically redacted from logs and diagnostics reports by `LogRotator.RedactSecrets`.
