# winqemufrontend

<img src="assets/brand/app-icon.svg" alt="QEMU Frontend icon" width="96" align="right" />

[Icon, wordmark, and brand assets](assets/brand/README.md)


A planned Windows-native consumer VM manager for QEMU (WinUI 3, C#), aiming to be the Windows equivalent of GNOME Boxes / UTM: create, install, run, snapshot and clone virtual machines without a terminal or QEMU syntax.

**Status: specification only — there is no code yet.**

- Product specification and acceptance tests: [docs/PRODUCT_SPEC.md](docs/PRODUCT_SPEC.md)
- Contributor and coding-agent instructions: [AGENTS.md](AGENTS.md)

## Roadmap

A first release that a normal Windows user could rely on, in order:

1. **App shell and QEMU detection:** a WinUI 3 window that finds an installed QEMU (or offers to install it), detects whether Windows Hypervisor Platform (WHPX) is enabled, and explains how to turn it on.
2. **Express "New VM from ISO":** pick an ISO, get a sensible preset (CPU, memory, disk size, UEFI) for Windows, Linux or BSD, and create the disk with `qemu-img`.
3. **Start, shut down and force off** over QMP, with the guest shown in QEMU's own window and the VM list showing live state.
4. **Edit a VM's hardware** (CPU, memory, disks, network, display) with validation against what the installed QEMU supports.
5. **Snapshots and cloning.**
6. **Embedded display** inside the app (later; the riskiest part of the spec).

Check early: Windows 11 guests require TPM 2.0, and a TPM emulator for QEMU on Windows is not well supported, so the Windows 11 preset may need the documented setup-time requirement bypass.

Building needs a Windows machine (WinUI 3 does not build on Linux or macOS).
