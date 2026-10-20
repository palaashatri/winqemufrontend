# AGENTS.md — winqemufrontend

A planned Windows-native, consumer-friendly VM manager for stock QEMU — "GNOME Boxes simplicity + UTM polish + virt-manager power" for Windows 11. Users should be able to create, install, run, snapshot and clone VMs without a terminal or QEMU syntax.

**There is no code yet.** The full product specification and acceptance tests are in [`docs/PRODUCT_SPEC.md`](docs/PRODUCT_SPEC.md) (§ numbers below refer to it). This file is the short instruction sheet. Work in milestones through reviewed PRs; the spec's "never stop / autonomous execution" wording (§89–90) no longer applies.

## Stack (settled)

- **C# on current .NET, Windows App SDK, WinUI 3, XAML** for ~90–95% of the code (§2). MVVM where it reduces coupling (§79).
- C++/WinRT only behind a narrow boundary where it clearly pays off: Direct3D guest framebuffer rendering, low-level input, high-throughput pixel upload (§2, §30).
- QEMU runs as a **separate process**; we never link or copy QEMU source (§6, §67).
- Acceleration: **WHPX** (Windows Hypervisor Platform) when available, TCG otherwise. **Never HAXM** — it is discontinued (§7–8).
- Control via **QMP** over a local-only transport: named pipe preferred, otherwise loopback TCP. Never bind `0.0.0.0`, never expose QMP on the LAN (§9–10).

## Target layout (§78)

```text
src/App/            WinUI application (views, view models)
src/Core/           VM model, configuration, presets, lifecycle, diagnostics — no UI dependencies
src/Qemu/           runtime discovery, capabilities, command builder, processes, QMP, guest agent, qemu-img
src/Storage/ Networking/ Firmware/ Security/ Integration/ Display/
src/Native/GuestDisplay/   C++/WinRT renderer (only if needed)
tests/              unit (Core/Qemu without XAML), fake QMP server, real-QEMU integration, UI automation
```

No circular dependencies; `Core` must be unit-testable without XAML.

## First milestones

1. **M0 Foundation** — solution + projects above, settings/persistence, logging, CI on `windows-latest` running `dotnet build` and `dotnet test`, packaging skeleton (MSIX).
2. **M1 QEMU runtime** — locate/manage a QEMU install, `qemu-img`, capability probing (`-accel help`, `-machine help`, `-device help`), WHPX detection, command generation with unit tests.
3. **M2–M3** — VM configuration model, presets, storage, UEFI firmware, user-mode networking, process lifecycle, a real QMP client with events (tested against a fake QMP server, §70).
4. **M4** — express "create VM from ISO" wizard that boots a guest. A good first public demo is M4 with the guest shown in QEMU's own window; the embedded display (M5) comes after.

When the solution exists, list the exact build/test/run commands in a **Commands** section here.

## Rules

1. **Don't fake features** (§87): no buttons that do nothing, no fake QEMU status or metrics, never report WHPX while running TCG, no snapshot UI without real disk snapshots, no Secure Boot/TPM claims unless the firmware and a TPM emulator are actually configured. Unfinished features are hidden from release UI.
2. Generate QEMU command lines from the structured VM model and probed capabilities, never from string templates with unsupported flags (§11–12, §19). Advanced users get a raw-arguments escape hatch (§18).
3. The app runs **unelevated**. Elevation only for narrow operations (e.g. enabling Windows Hypervisor Platform), ideally via a separate helper (§57).
4. Minimise dependencies; each needs a reason, licence and maintenance check (§80). Distributing QEMU, firmware or TPM-emulator binaries means preserving licences and meeting GPL source obligations (§67).
5. Accessibility, DPI scaling and keyboard navigation are release blockers, not polish (§61–62).
6. Verify Windows App SDK / WinUI / QEMU APIs against current official docs before using them (§3, §91).

## Git workflow

- Never push to `main`. One milestone slice per branch (`agent/<milestone>-<topic>`), PR with the commands you ran and their results.
- Real-hardware checks (WHPX on Intel and AMD, Windows on Arm) can't run in CI; say in the PR what was and wasn't tested.
