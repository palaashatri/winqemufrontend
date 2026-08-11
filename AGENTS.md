# ONE-SHOT IMPLEMENTATION GOAL — Build the GNOME Boxes / UTM of Windows

You are responsible for designing, implementing, testing, packaging, and productizing a **production-quality, Windows-native graphical frontend and complete consumer VM manager for QEMU**.

Do not stop after planning, scaffolding, mockups, prototypes, or partial implementation.

Continue implementing until the repository contains a coherent, usable application that a normal Windows user can install, launch, create a virtual machine with, install an operating system into, shut down, restart, snapshot, clone, configure, and use without ever opening a terminal or learning QEMU command-line syntax.

The product vision is:

> **GNOME Boxes simplicity + UTM polish + virt-manager power + first-class Windows 11 integration, powered by stock QEMU.**

The product is specifically intended to fill the gap on Windows.

Linux already has GNOME Boxes and virt-manager.
macOS already has UTM.

This application should become the equivalent consumer-grade QEMU experience for Windows.

---

# 0. NON-NEGOTIABLE PRODUCT PRINCIPLE

The primary workflow must be:

```text
Install application
       ↓
Launch application
       ↓
New Virtual Machine
       ↓
Choose ISO / operating system
       ↓
Application recommends configuration
       ↓
Create
       ↓
VM boots
       ↓
Install/use operating system
```

A new user should not need to understand:

* QEMU command lines
* machine types
* WHPX
* TCG
* OVMF
* QMP
* qcow2
* VirtIO
* SLIRP
* SPICE
* VNC
* virtual CPU models

Those facilities should still be available to expert users through an Advanced mode.

The default UX must optimize for:

> **“I have an ISO. Make me a VM.”**

---

# 1. PLATFORM SCOPE

This is deliberately a Windows application.

Do NOT compromise the architecture merely to obtain hypothetical Linux/macOS portability.

Target:

* Windows 11 as the primary platform
* current supported x86-64 Windows 11 systems
* Windows-on-Arm as an architectural consideration where QEMU and Windows APIs support it

Use native Windows facilities aggressively.

Do not use:

* Electron
* Tauri
* React
* WebView UI
* Flutter
* Qt
* GTK
* JavaFX
* Avalonia
* a browser-based frontend

This must look, behave and integrate like a genuine modern Windows application.

---

# 2. REQUIRED APPLICATION STACK

Use the latest production-supported versions available at implementation time of:

```text
C#
.NET
Windows App SDK
WinUI 3
XAML
```

Use C# for essentially the entire application.

Use native C++ / C++/WinRT only where it provides a concrete technical advantage, especially around:

* Direct3D
* guest framebuffer rendering
* low-level input
* Win32 interoperability
* high-performance pixel upload
* device APIs

Do NOT introduce C++ merely because this is a virtualization application.

Expected division:

```text
~90–95% C#
~5–10% C++/WinRT if actually necessary
```

Maintain a strict component boundary around native code.

---

# 3. USE CURRENT MICROSOFT PLATFORM GUIDANCE

Before implementing platform-specific functionality, inspect the current official Microsoft documentation.

Use the latest **stable**, production-supported Windows App SDK APIs.

Do not depend on Preview or Experimental Windows App SDK APIs for essential application functionality unless there is no viable stable alternative.

If newer stable APIs have superseded anything specified here, use the newer stable API and document the decision.

---

# 4. WINDOWS 11 MUST BE A FIRST-CLASS DESIGN TARGET

The application must not merely run on Windows 11.

It must feel like it belongs there.

Use appropriate native Windows 11 functionality including, where supported:

* WinUI 3
* Fluent Design
* system theme integration
* light mode
* dark mode
* system accent color
* Mica/system backdrops
* Acrylic for appropriate transient surfaces
* modern native dialogs
* modern context menus
* native title bars
* integrated custom title-bar content where appropriate
* correct Windows caption buttons
* Snap Layout compatibility
* Win+Z behavior
* correct maximize-button hover behavior
* rounded Windows
* Windows animations
* high-DPI rendering
* per-monitor DPI awareness
* native file pickers
* drag and drop
* clipboard APIs
* accessibility
* Narrator
* keyboard navigation
* focus indicators
* high-contrast themes
* touch input
* precision touchpads
* notifications
* application lifecycle/activation APIs
* file associations
* protocol activation where useful
* taskbar integration
* taskbar progress where applicable
* Jump Lists where they improve VM launching
* Windows sharing/integration APIs where appropriate
* proper Windows power/session handling
* suspend/resume awareness
* application recovery where appropriate
* modern packaging

Do not fake Windows functionality using custom CSS-like replicas.

Follow Microsoft's current Windows application design and interaction guidance.

---

# 5. UI QUALITY BAR

This is a consumer product.

Do NOT produce an administration console covered in tables.

The VM library should be visual, calm, simple and immediately understandable.

Reference product philosophy:

```text
GNOME Boxes
UTM
Windows 11 Settings
Windows Terminal
modern Microsoft first-party Windows applications
```

Do not copy trademarks, proprietary artwork, icons or layouts exactly.

Build an original Fluent-based design.

The application should open approximately like:

```text
┌──────────────────────────────────────────────────────┐
│  Virtual Machines                         ＋ New     │
│                                                      │
│  Home                                                │
│                                                      │
│  ┌──────────────────┐  ┌──────────────────┐         │
│  │                  │  │                  │         │
│  │   Fedora 43      │  │   Windows 11     │         │
│  │                  │  │                  │         │
│  │   ● Running      │  │   ○ Stopped      │         │
│  │                  │  │                  │         │
│  │  8 GB · 8 CPUs   │  │ 16 GB · 8 CPUs   │         │
│  └──────────────────┘  └──────────────────┘         │
│                                                      │
│  ┌──────────────────┐                               │
│  │   FreeBSD        │                               │
│  │   ○ Stopped      │                               │
│  └──────────────────┘                               │
│                                                      │
└──────────────────────────────────────────────────────┘
```

VM cards should support useful preview information without becoming cluttered.

Support list and grid modes if appropriate.

Animations should be smooth, restrained and native-feeling.

Do not animate purely for spectacle.

---

# 6. QEMU IS THE VIRTUALIZATION ENGINE

Do NOT implement a hypervisor.

Do NOT fork QEMU unless an unavoidable technical blocker is demonstrated and documented.

Use current stable stock QEMU.

The application should support management of the QEMU runtime itself.

The consumer should not be required to separately install QEMU manually.

Develop a proper QEMU runtime-management subsystem capable of:

* detecting bundled QEMU
* validating QEMU
* determining QEMU version
* detecting available targets
* querying supported machines
* querying CPUs
* querying devices
* querying accelerators
* querying display capabilities
* locating `qemu-img`
* validating firmware
* safely updating the managed QEMU runtime in future
* reporting runtime health

If QEMU binaries are redistributed with the application, perform a proper licensing audit and satisfy all QEMU/dependency redistribution obligations.

Do not hide licensing notices.

Do not bundle operating-system ISOs.

---

# 7. HARDWARE ACCELERATION — WHPX, NOT HAXM

IMPORTANT:

**Do NOT use Intel HAXM.**

HAXM is obsolete and discontinued.

On Windows, the primary QEMU hardware-acceleration backend must be:

```text
WHPX
Windows Hypervisor Platform
```

For same-architecture virtualization:

```text
x86-64 Windows host
        ↓
x86/x86-64 guest
        ↓
WHPX whenever supported
```

On supported Windows-on-Arm configurations, use WHPX according to current QEMU capabilities.

For cross-architecture emulation or where WHPX cannot be used:

```text
TCG
```

Use:

```text
WHPX → preferred
TCG  → fallback/emulation
```

Never silently give the user an unexpectedly slow TCG VM when WHPX should have been available.

Instead detect acceleration status.

Expose something consumer-friendly like:

```text
Hardware acceleration
✓ Enabled
```

or:

```text
Hardware acceleration unavailable

Windows Hypervisor Platform is not enabled.

[ Enable ]
[ Learn more ]
```

If enabling a Windows optional feature requires elevation:

* explain exactly what will happen
* request elevation only for that operation
* never run the whole application elevated
* verify success
* explain whether Windows must reboot

The application must remain least-privilege by default.

---

# 8. ACCELERATION DETECTION

Implement robust startup diagnostics.

Detect:

* host architecture
* virtualization support
* Windows Hypervisor Platform availability
* QEMU WHPX availability
* whether WHPX actually initializes successfully
* TCG availability

Do not simply assume that because Windows exposes virtualization settings, QEMU can successfully start WHPX.

Implement an actual lightweight QEMU capability/smoke probe where necessary.

The application must distinguish:

```text
Supported
Enabled
Available
Working
Unavailable
Misconfigured
```

---

# 9. QEMU MACHINE PROTOCOL IS REQUIRED

Do NOT manage running VMs by scraping stdout.

Use **QMP — QEMU Machine Protocol** as the authoritative runtime-management interface.

Implement a typed asynchronous QMP client.

Required capabilities include:

* greeting negotiation
* `qmp_capabilities`
* request IDs
* concurrent requests
* JSON parsing
* typed responses
* asynchronous event handling
* disconnect detection
* timeout handling
* cancellation
* reconnect/recovery logic where sensible
* logging
* error mapping

VM states should come from actual QEMU state/events.

Represent at least:

```text
Created
Stopped
Starting
Running
Paused
Suspended
ShuttingDown
Stopping
Crashed
Unavailable
```

Use QMP for relevant operations such as:

* query status
* pause
* resume
* graceful powerdown
* reset
* quit
* removable media
* supported hotplug
* block operations
* snapshots where appropriate
* querying guest state

Use QEMU Guest Agent separately for guest-aware integration.

QMP and QGA are different protocols and must not be conflated.

---

# 10. SECURE LOCAL QMP TRANSPORT

QMP must never listen publicly.

Prefer a secure Windows-local transport supported by the selected QEMU build.

If named pipes can reliably provide the required QEMU/QMP semantics, prefer them.

If loopback TCP is required:

* bind to loopback only
* allocate ports safely
* never bind 0.0.0.0
* minimize connection lifetime
* prevent accidental remote exposure
* document local threat considerations

Never expose unauthenticated QMP over the LAN.

---

# 11. STRUCTURED VM MODEL

Never store VM configuration merely as a string of QEMU arguments.

Define a versioned domain model.

Conceptually:

```text
VirtualMachine
├── identity
├── metadata
├── guest profile
├── architecture
├── machine
├── accelerator
├── CPU
├── memory
├── firmware
├── security
├── storage[]
├── network[]
├── graphics
├── audio
├── input
├── USB
├── shared services
├── integration
├── boot
├── advanced QEMU args[]
└── schema version
```

Persist using a well-defined human-inspectable configuration format such as JSON.

Provide schema migration.

Never corrupt an old VM because the application was upgraded.

Write configuration atomically.

---

# 12. CAPABILITY-DRIVEN QEMU CONFIGURATION

Do not hardcode a giant permanent list of QEMU capabilities.

Build a QEMU capability probing layer.

Where appropriate interrogate installed QEMU for:

* architectures
* machines
* accelerators
* CPUs
* devices
* network models
* block devices
* graphics devices
* audio backends
* firmware-related capabilities

Cache results by QEMU version.

Invalidate the cache when QEMU changes.

Keep known-good product presets separately from raw capability discovery.

---

# 13. EXPRESS VM CREATION

The normal consumer workflow should be extremely short.

## Page 1

```text
Create a Virtual Machine

[ Install from ISO ]

or

[ Emulate another architecture ]
```

Allow drag/drop of an ISO onto the window.

---

# 14. ISO / OS RECOGNITION

Attempt to identify common installer media automatically where practical and reliable.

Recognize enough information to infer:

* Windows
* common Linux distributions
* BSD
* architecture
* likely firmware
* OS family

Never confidently invent an OS identity from weak evidence.

If uncertain:

```text
Operating system could not be identified.

[ Linux ]
[ Windows ]
[ BSD ]
[ Other ]
```

The user can continue regardless.

---

# 15. RECOMMENDED VM PRESETS

Maintain curated presets for common workloads.

At minimum:

## Windows 11

Provide:

* modern Q35-style machine where appropriate
* UEFI
* Secure Boot capability where available
* software TPM 2.0 integration where technically and legally available
* sane virtual CPU topology
* accelerated virtualization via WHPX
* recommended storage
* networking
* display
* audio
* USB/input
* Windows-friendly devices

A Windows 11 VM should not require users to bypass platform requirements just because our VM manager failed to implement virtual hardware properly.

Investigate the current supported QEMU + Windows mechanism for TPM 2.0, including current software TPM options.

Implement a proper supported solution.

Do not silently disable Windows 11 requirements.

## Windows 10

Provide an appropriate compatibility-oriented preset.

## Linux

Provide modern VirtIO-oriented presets.

Support common distributions including at least:

* Ubuntu
* Fedora
* Debian
* Arch-derived systems
* Linux Mint

Do not tightly couple the product to these distributions.

## BSD

Provide safe presets for:

* FreeBSD
* OpenBSD where practical

## Generic

Provide:

* Generic PC
* Generic ARM64
* Generic RISC-V

---

# 16. USER-FACING RESOURCE CONFIGURATION

Normal mode should expose only:

```text
Memory
CPU
Disk size
```

with excellent recommended defaults.

Show understandable explanations.

For example:

```text
Memory

Recommended: 8 GB

Host memory: 32 GB

────────────●────────────

8 GB
```

Prevent obviously destructive configurations.

Do not reserve so much host RAM that Windows becomes unusable without warning the user.

---

# 17. ADVANCED HARDWARE EDITOR

Expert users must have a powerful Advanced mode comparable in spirit to virt-manager.

Expose:

## Overview

* name
* description
* tags
* UUID

## System

* architecture
* machine type
* chipset
* firmware

## CPU

* vCPU count
* sockets
* cores
* threads
* CPU model
* advanced CPU flags

## Memory

* RAM
* ballooning where supported

## Storage

* controller
* disk format
* bus
* cache mode
* discard
* removable media

## Network

* adapters
* model
* MAC address
* user-mode NAT
* port forwarding
* advanced networking

## Display

* virtual GPU
* display transport
* resolution/scaling behavior
* acceleration options exposed by the actual backend

## Audio

* guest device
* host backend

## Input

* tablet
* mouse
* keyboard

## USB

* controllers
* attached devices

## Boot

* boot order
* temporary boot override

## Integration

* clipboard
* guest agent
* file sharing when available

## Security

* TPM
* Secure Boot
* isolation-related options

## Developer

* serial console
* QMP
* monitor
* GDB
* kernel boot
* initrd
* kernel command line
* pause at startup
* QEMU logging

---

# 18. RAW QEMU ESCAPE HATCH

Advanced users must never be trapped by the GUI.

Provide:

```text
Advanced → Additional QEMU Arguments
```

Validate these separately from generated arguments.

Clearly indicate that custom arguments may override safe assumptions.

Also provide:

```text
Diagnostics → Generated QEMU Command
```

with:

* formatted command
* copy button
* expanded arguments
* environment
* QEMU version

This is essential for debugging and power users.

---

# 19. COMMAND GENERATION

Create a dedicated typed QEMU command builder.

Never build commands by string concatenation.

Use `ProcessStartInfo.ArgumentList` or equivalent safe argument APIs.

Handle:

* spaces
* Unicode paths
* quotes
* unusual filenames

No shell invocation should be necessary to start QEMU.

Command generation must be deterministic and unit-tested.

---

# 20. STORAGE

Default virtual disks to QCOW2 unless another format provides a specific advantage.

Support:

* qcow2
* raw
* VHD/VHDX where QEMU supports the required workflow
* VMDK import/export where appropriate

Use `qemu-img` through a typed wrapper.

Implement:

* create
* inspect
* resize
* convert
* check
* clone
* compact where safely possible

Never perform destructive image operations without confirmation.

Display:

```text
Virtual capacity: 128 GB
Actual host usage: 17.4 GB
Format: QCOW2
```

where information is available.

---

# 21. VM DIRECTORY LAYOUT

Keep every VM self-contained unless the user chooses otherwise.

Example:

```text
Virtual Machines/
└── Fedora 43/
    ├── vm.json
    ├── disks/
    │   └── system.qcow2
    ├── nvram/
    │   └── vars.fd
    ├── security/
    │   └── tpm/
    ├── snapshots/
    ├── logs/
    └── screenshots/
```

Support moving VMs.

Use stable IDs rather than relying on folder names.

---

# 22. UEFI

Modern guests should use UEFI by default when appropriate.

Manage guest-specific writable UEFI variable stores correctly.

Never have multiple VMs mutate a shared firmware variables image.

Use appropriate OVMF/EDK2 firmware for x86/x64.

Use appropriate firmware for other architectures.

Support legacy BIOS only where useful.

---

# 23. SECURE BOOT

Where supported by the selected firmware/runtime combination:

* expose Secure Boot
* provide it automatically for presets that require it
* maintain VM-specific variable storage
* do not fake the state

Clearly report unsupported configurations.

---

# 24. TPM 2.0

Implement a proper software TPM abstraction for guests that require it.

Windows 11 is the primary motivating case.

Research the current production-supported Windows-host mechanism for connecting QEMU's TPM device model to a software TPM implementation.

Requirements:

* per-VM TPM state
* TPM 2.0
* lifecycle management
* clean shutdown
* snapshot implications documented and handled
* no global shared TPM state
* no command-window flashes

Treat the TPM helper as a managed VM dependency.

---

# 25. DEFAULT NETWORKING

Consumer VMs must work immediately without administrator privileges.

Default to QEMU's safe user-mode networking/NAT capability when appropriate.

Expected result:

```text
Guest boots
↓
DHCP
↓
Internet works
```

No TAP adapter setup should be required for normal users.

---

# 26. ADVANCED NETWORKING

Provide advanced support for:

* NAT
* port forwarding
* isolated networking where practical
* bridged/TAP networking where supported and intentionally configured

Any operation requiring:

* driver installation
* elevated privilege
* firewall changes
* network adapter modification

must be explicit and user-authorized.

---

# 27. PORT FORWARDING UI

Do not expose raw syntax.

Provide:

```text
Port Forwarding

Name       Protocol   Host      Guest
SSH        TCP        2222      22
Web        TCP        8080      80

[ + Add ]
```

Generate safe QEMU networking arguments internally.

---

# 28. EMBEDDED GUEST DISPLAY IS MANDATORY

Do NOT merely launch QEMU's SDL/GTK window beside the manager.

That would make this a configuration utility, not a consumer VM application.

The VM must appear inside our application:

```text
┌───────────────────────────────────────────────┐
│ Fedora 43                             ─ □ ×  │
├───────────────────────────────────────────────┤
│                                               │
│                                               │
│              Guest framebuffer                │
│                                               │
│                                               │
├───────────────────────────────────────────────┤
│ ⏻  ⏸  ↻      USB ▾     CD ▾        ⋯        │
└───────────────────────────────────────────────┘
```

---

# 29. DISPLAY TRANSPORT

Start with a production-quality transport supported by QEMU on Windows.

VNC is acceptable as the initial universally available embedded-display transport.

Prefer:

```text
QEMU
 ↓
localhost-only VNC
 ↓
our VNC client
 ↓
native accelerated rendering surface
```

Do not expose the VNC server outside localhost.

Implement enough of the protocol properly for good desktop usability.

Then evaluate SPICE as a higher-functionality transport where the selected Windows QEMU build supports it reliably.

The abstraction must make future display transports replaceable.

---

# 30. NATIVE GUEST RENDERER

Do not repeatedly copy giant framebuffers through XAML objects if that creates poor performance.

Create a dedicated rendering layer.

Where beneficial use:

```text
C++/WinRT
+
Direct3D 11/12
+
native WinUI composition surface
```

or the current Microsoft-recommended equivalent.

Requirements:

* efficient framebuffer updates
* dirty-region updates
* hardware scaling
* smooth resizing
* fullscreen
* aspect-ratio preservation
* optional stretch
* HiDPI awareness
* low latency
* correct cursor rendering
* multi-monitor-ready architecture

Do not prematurely optimize without profiling, but the target must support interactive desktop VMs smoothly.

---

# 31. INPUT

Guest input must feel normal.

Implement:

* keyboard
* mouse
* absolute pointer where supported
* relative pointer where required
* mouse capture/release
* Windows key handling
* Ctrl/Alt combinations
* configurable host-key combination
* clipboard shortcuts
* fullscreen shortcut

Avoid inverted axes, lost modifier state and stuck keys.

Input handling requires automated and manual testing.

---

# 32. CLIPBOARD

Implement bidirectional clipboard integration where supported.

Start with display-protocol clipboard facilities if appropriate.

Enhance through QEMU Guest Agent or other supported guest integration where useful.

Clipboard failures must never crash or hang the VM.

---

# 33. AUDIO

Guest audio should work automatically with normal presets.

Probe the actual QEMU Windows audio backend capabilities.

Select the best supported Windows backend automatically.

Do not hardcode a backend that the packaged QEMU runtime does not contain.

Support:

* output
* input where practical
* mute
* volume integration where appropriate

---

# 34. REMOVABLE MEDIA

From the running VM toolbar:

```text
CD/DVD
────────────
Windows.iso ✓

Eject

Choose image...
```

Use QMP for safe media changes where supported.

Allow booting from ISO without editing command lines.

---

# 35. USB

Design a proper USB subsystem.

Support:

* standard virtual USB controllers
* adding virtual USB devices
* host USB passthrough where supported by the Windows QEMU build
* safe attach/detach
* QMP hotplug where available

Expose understandable device names.

Do not show only VID/PID unless no friendly information exists.

Never capture host-critical devices accidentally without warning, especially:

* primary keyboard
* primary mouse
* storage devices

---

# 36. QEMU GUEST AGENT

Support QEMU Guest Agent as an optional enhancement.

Detect whether QGA is available.

Use it where appropriate for features such as:

* graceful shutdown
* guest information
* IP discovery
* filesystem freeze/thaw around backups where applicable
* improved integration

Do not require QGA merely to boot or operate a VM.

---

# 37. UNATTENDED INSTALLATION

Implement an **Express Installation** framework.

When an operating system supports reliable automated installation, allow:

```text
Create VM
↓
Name
↓
Optional username/password
↓
Install
↓
User returns to installed OS
```

Potential mechanisms include:

* Windows unattended installation answer files
* cloud-init
* autoinstall
* kickstart
* preseed where still applicable

Only implement workflows that are technically and legally sound.

Store credentials securely and minimize their lifetime.

Never leave plaintext passwords sitting in logs or permanent temporary files.

Provide:

```text
Express Installation
```

and:

```text
Manual Installation
```

---

# 38. OS DOWNLOADS

The application may offer convenient links/download workflows only for operating systems whose distribution permits it.

Where supported:

* retrieve from official distribution infrastructure
* verify cryptographic checksums/signatures where available
* clearly show source
* cache intelligently
* allow deleting downloads

Never host or redistribute proprietary Windows installation media without authorization.

For Windows, support user-provided legitimate ISO media and whatever official Microsoft acquisition mechanism current licensing permits.

---

# 39. VIRTIO

Use VirtIO when it materially improves performance.

However, do not build a Windows installation preset that cannot boot because required storage/network drivers are missing.

Develop a coherent Windows VirtIO strategy:

* use inbox-compatible virtual hardware during installation when necessary
* optionally attach trusted signed VirtIO driver media
* migrate to optimized hardware safely where useful

If VirtIO drivers are downloaded or redistributed:

* use authoritative sources
* verify integrity
* audit licensing
* never download random binaries from third-party mirrors

---

# 40. START / STOP UX

VM cards should provide obvious controls.

Stopped:

```text
[ ▶ Start ]
```

Running:

```text
[ Open ]
```

Context menu:

```text
Open
Pause
Restart
Shut Down
Force Stop
──────────
Snapshot
Clone
Settings
Export
Delete
```

Destructive actions must be visually differentiated and confirmed appropriately.

---

# 41. GRACEFUL POWER MANAGEMENT

"Shut Down" should request graceful guest shutdown first.

"Force Stop" should behave like removing power.

Do not make them equivalent.

Use QMP/QGA capabilities appropriately.

Represent pending shutdown state in the UI.

---

# 42. VM WINDOW

A running VM should be optionally detachable into its own window.

Support multiple running VM windows.

A VM can remain running when its display window is closed if the user preference allows it.

Closing the application while VMs are running must produce a deliberate policy:

```text
2 virtual machines are still running.

○ Keep them running
○ Shut them down
○ Force stop

Remember my choice

[ Cancel ] [ Continue ]
```

Never accidentally destroy guest state because the manager window closed.

---

# 43. FULLSCREEN

Implement polished fullscreen behavior.

Requirements:

* seamless enter/exit
* host shortcut
* toolbar reveal
* no mouse trapping bugs
* correct scaling
* multi-monitor correctness
* Windows notifications should not unexpectedly break the state

---

# 44. SNAPSHOTS

Snapshots must be a first-class feature.

UI concept:

```text
Snapshots

● Current
│
├── Today 11:42
│   Graphics drivers installed
│
├── Today 09:17
│   Clean installation
│
└── Yesterday
    Before updates
```

Support:

* create
* restore
* rename
* delete
* clone from snapshot

Use QEMU/qcow2 snapshot mechanisms safely.

Implement proper runtime restrictions.

Never pretend a snapshot succeeded until QEMU/storage confirms it.

---

# 45. CLONING

Allow:

```text
Clone VM

○ Full clone
○ Linked clone
```

Implement linked clones only if their backing-file relationships can be made robust and understandable.

Never allow users to accidentally delete a parent disk while children depend on it.

Track dependency graphs explicitly.

---

# 46. IMPORT

Support importing useful disk/VM forms where practical:

* qcow2
* raw
* VHD/VHDX
* VMDK

Allow:

```text
Import existing disk
```

Infer safe defaults, then let the user choose OS/architecture if detection is uncertain.

---

# 47. EXPORT

Provide a portable export format for our own application.

Example:

```text
MyVM.qvm
```

or a directory/package format.

The exported form should contain:

* VM config
* disk images
* UEFI vars
* TPM state where appropriate
* metadata
* optional snapshots

Do not embed host-specific transient paths.

Document portability constraints.

---

# 48. SETTINGS

Global settings should include:

## General

* VM storage location
* launch behavior
* update behavior

## Appearance

* System
* Light
* Dark

## Virtualization

* QEMU version
* QEMU runtime status
* WHPX status
* TCG status

## Display

* scaling preference
* fullscreen behavior

## Input

* host key
* capture behavior

## Integration

* clipboard
* shared services

## Advanced

* logging
* QEMU runtime
* experimental opt-ins if any

---

# 49. DIAGNOSTICS

This application should be exceptionally easy to troubleshoot.

Provide:

```text
Help → Diagnostics
```

Show:

* app version
* Windows version
* Windows architecture
* QEMU version
* available accelerators
* WHPX probe result
* firmware versions
* VM storage location
* relevant runtime components

Provide:

```text
Copy diagnostics
```

Sanitize:

* usernames where unnecessary
* credentials
* tokens
* secret paths
* passwords

---

# 50. QEMU LOGS

Maintain per-VM runtime logs.

Do not flood the disk indefinitely.

Implement log rotation.

Advanced users should be able to inspect:

* QEMU stdout/stderr
* QMP events
* lifecycle events
* generated command
* application errors

Never log passwords or authentication material.

---

# 51. DEVELOPER MODE

Include an optional VM **Developer Mode** particularly useful for OS development.

Expose:

```text
Serial Console
QEMU Monitor
QMP Inspector
GDB Server
Pause CPU at startup
Direct kernel boot
Initrd
Kernel command line
Debug logs
Record/replay if supported
```

These tools must not pollute the normal consumer experience.

---

# 52. SERIAL CONSOLE

Provide an integrated serial terminal.

Support:

* copy/paste
* searchable history
* save log
* monospace rendering
* ANSI terminal behavior where appropriate

This is valuable for:

* Linux boot debugging
* kernel development
* BSD
* custom operating systems

---

# 53. QMP INSPECTOR

Developer mode may expose:

```text
QMP Inspector

> query-status
```

with formatted JSON response.

Protect the user from accidentally sending destructive commands unless they deliberately enable raw mode.

---

# 54. PERFORMANCE

The manager itself must remain lightweight.

Targets:

* fast startup
* no browser runtime
* low idle CPU
* sensible idle memory
* no polling loops when event-driven APIs are available
* asynchronous disk/network/process work
* UI thread never blocked by QEMU operations

Use async/await appropriately.

Do not abuse `Task.Run`.

Use cancellation correctly.

---

# 55. HOST RESOURCE MONITORING

For running VMs, show useful optional metrics:

```text
CPU
Memory
Disk activity
Network activity
```

Clearly distinguish:

* configured guest memory
* host process working set
* host CPU usage

Do not invent guest metrics when QEMU cannot provide them.

---

# 56. SECURITY

Treat disk images, ISO images, VM files and imported configurations as untrusted input.

Threat model the application.

At minimum protect against:

* path traversal
* malicious config files
* unsafe archive extraction
* command injection
* argument injection
* malicious filenames
* unbounded allocation from malformed metadata
* public VNC exposure
* public QMP exposure
* arbitrary helper execution
* insecure downloads
* checksum bypass
* symlink/reparse-point surprises where relevant
* accidental privilege escalation

Never launch QEMU through `cmd.exe` merely to simplify argument handling.

---

# 57. PRIVILEGE MODEL

The main process runs unelevated.

Privilege elevation is allowed only for narrow explicit operations such as:

* enabling Windows optional virtualization components
* installing an approved networking component
* other unavoidable system configuration

Separate elevated helpers if appropriate.

Never require "Run as Administrator" just to use normal VMs.

---

# 58. CRASH RECOVERY

If the GUI crashes while QEMU continues running:

* preserve enough metadata to discover the managed QEMU process
* attempt safe reattachment where technically feasible
* otherwise clearly explain state
* never launch a second conflicting QEMU instance for the same VM

Use lock files/process identity carefully.

Handle stale locks.

---

# 59. APP RESTART / WINDOWS REBOOT

Handle:

* Windows shutdown
* Windows restart
* user sign-out
* host sleep
* host hibernate
* application update

Do not silently corrupt a running VM.

Implement appropriate notifications and shutdown behavior.

---

# 60. SEARCH

The VM library should support search when enough VMs exist.

Search:

* VM name
* OS
* tags

Keep it unobtrusive for small collections.

---

# 61. ACCESSIBILITY IS A RELEASE BLOCKER

Every primary function must be usable using:

* keyboard only
* Windows Narrator
* high contrast
* 200% scaling

Implement:

* AutomationProperties
* accessible names
* logical focus order
* visible focus
* suitable hit targets
* proper semantic controls

Do not ship icon-only buttons without accessible names/tooltips.

---

# 62. DPI AND DISPLAY QA

Test at:

```text
100%
125%
150%
175%
200%
```

Test:

* single monitor
* multi-monitor
* different DPI per monitor
* portrait
* ultrawide
* small snapped layouts

The application must not become unusable when placed in Windows 11 1/3 or 1/4 Snap Layout regions.

---

# 63. RESPONSIVE WINDOWS DESIGN

Do not assume the application is always maximized.

Design adaptive layouts.

For example:

```text
wide:
navigation sidebar + content

medium:
compact navigation + content

narrow:
collapsed navigation
single-column VM cards
```

---

# 64. PACKAGING

Produce a professional Windows installation experience.

Prefer current Microsoft-recommended packaging.

Support as appropriate:

* MSIX
* signed installer
* Start menu
* uninstall
* repair/update behavior
* file associations
* protocol activation
* clean removal

No terminal window should flash when launching the application or starting QEMU/helper processes.

---

# 65. APPLICATION UPDATE SYSTEM

Implement a secure update strategy.

Separate:

```text
Application updates
QEMU runtime updates
```

Never replace QEMU binaries while VMs are running.

Cryptographically verify downloaded update artifacts.

---

# 66. MICROSOFT STORE / WINGET READINESS

Structure packaging so the application can eventually be distributed through:

* Microsoft Store where policy permits
* WinGet
* GitHub Releases or equivalent signed release channel

Generate the metadata/manifests necessary for straightforward publication.

Do not require Store installation to work.

---

# 67. QEMU LICENSING

Perform a real dependency/license inventory.

Our application should communicate with QEMU as a separate executable/process.

Do not copy arbitrary QEMU source into our application.

If distributing QEMU binaries:

* preserve licenses
* preserve notices
* satisfy source-code obligations
* document the exact QEMU version
* document included third-party components

Do the same for:

* firmware
* TPM components
* VirtIO components
* display libraries
* native dependencies

Do not make unsupported legal claims in documentation.

---

# 68. TEST ARCHITECTURE

Testing is mandatory.

Create:

```text
Unit tests
Integration tests
QEMU integration tests
UI tests
End-to-end tests
Migration tests
Security regression tests
```

---

# 69. UNIT TESTS

Test at minimum:

* VM model serialization
* schema migration
* command generation
* path handling
* QEMU argument escaping
* OS presets
* resource recommendations
* disk configuration
* networking configuration
* port forwarding
* capability parsing
* QMP serialization
* QMP event handling
* lifecycle state machine
* error mapping

---

# 70. FAKE QMP SERVER

Build a deterministic fake QMP test server.

Test:

* valid negotiation
* malformed JSON
* events arriving between responses
* out-of-order responses
* missing request IDs
* timeout
* disconnect
* crash
* rapid events
* unexpected QEMU responses

Do not rely solely on happy-path QEMU testing.

---

# 71. REAL QEMU INTEGRATION TESTS

Run headless real-QEMU tests using TCG so tests do not universally depend on nested hardware virtualization.

Test:

```text
create disk
generate config
start VM
connect QMP
query status
pause
resume
reset
power down/quit
validate process exit
```

Where possible create a tiny deterministic test guest.

---

# 72. WHPX TESTS

Hardware-acceleration tests must exist separately.

On compatible Windows hardware/self-hosted CI:

* start QEMU with WHPX
* validate successful initialization
* boot guest
* verify QMP lifecycle
* run sustained smoke test

Do not mark WHPX as tested because TCG tests passed.

---

# 73. UI AUTOMATION

Use the currently supported Windows UI automation stack.

Test:

```text
launch app
create VM
select ISO
change RAM
create
start
open VM
pause
resume
stop
settings
delete
```

Automate keyboard navigation.

---

# 74. VISUAL QA

Capture screenshots automatically for important screens.

Review:

* light
* dark
* high contrast
* 100%
* 150%
* 200%
* snapped narrow
* normal
* ultrawide

Detect:

* clipping
* overlapping
* bad text wrapping
* inconsistent margins
* unreadable contrast
* incorrect focus states
* misplaced dialogs

---

# 75. PERFORMANCE QA

Profile:

* cold startup
* warm startup
* VM creation
* VM launch
* resize
* framebuffer rendering
* high-motion guest desktop
* idle running VM
* multiple VMs

Measure rather than guessing.

---

# 76. RELIABILITY QA

Stress:

```text
start/stop VM repeatedly
pause/resume repeatedly
insert/eject media
resize display repeatedly
rapidly close/reopen VM window
host sleep/resume
network disconnect
QEMU crash
full disk
corrupt qcow2
missing firmware
missing QEMU
broken WHPX
```

Failures must produce actionable UI rather than raw exceptions.

---

# 77. ERROR UX

Bad:

```text
QEMU exited with code 1.
```

Good:

```text
Hardware acceleration could not start.

Windows Hypervisor Platform appears to be disabled.

[ Enable Hardware Acceleration ]
[ Start Using Software Emulation ]
[ View Details ]
```

Keep technical details accessible behind:

```text
View Details
```

---

# 78. PROJECT STRUCTURE

Use a modular solution resembling:

```text
src/
├── App/
│   └── WinUI application
│
├── Core/
│   ├── VirtualMachines
│   ├── Configuration
│   ├── Presets
│   ├── Lifecycle
│   └── Diagnostics
│
├── Qemu/
│   ├── Runtime
│   ├── Capabilities
│   ├── CommandBuilder
│   ├── Processes
│   ├── Qmp
│   ├── GuestAgent
│   └── QemuImg
│
├── Storage/
├── Networking/
├── Firmware/
├── Security/
├── Integration/
├── Display/
│
└── Native/
    └── GuestDisplay
```

Do not create circular dependencies.

Core VM models must not depend on UI classes.

---

# 79. MVVM

Use a coherent modern WinUI application architecture.

MVVM is acceptable and recommended where it reduces coupling.

Avoid turning every trivial control into unnecessary abstraction.

The business/domain layer must be unit-testable without XAML.

---

# 80. DEPENDENCIES

Minimize external dependencies.

Every dependency must justify:

* why it exists
* license
* maintenance state
* security implications

Do not add huge frameworks for trivial tasks.

Prefer Windows/.NET platform APIs where they are appropriate.

---

# 81. DOCUMENTATION

Maintain concise documentation covering:

```text
README
architecture
building
testing
QEMU/runtime licensing
security
VM format
release process
```

Do not use documentation to conceal missing functionality.

Statements such as:

```text
Implemented
Supported
Production ready
```

must correspond to working code and passing tests.

---

# 82. CONSUMER EXPERIENCE ACCEPTANCE TEST

A nontechnical Windows user must be able to:

1. Download installer.
2. Install application.
3. Launch it.
4. Press `New`.
5. Pick an Ubuntu/Fedora/Windows ISO.
6. Accept recommended resources.
7. Press `Create`.
8. See the guest boot inside the application.
9. Complete installation.
10. Reboot the guest.
11. Use network and audio.
12. Enter fullscreen.
13. Shut the VM down.
14. Relaunch the application.
15. Start the VM again.
16. Create a snapshot.
17. Restore the snapshot.
18. Clone the VM.
19. Delete the clone.

without:

* PowerShell
* CMD
* manually editing JSON
* manually installing QEMU
* manually creating qcow2 disks
* typing QEMU flags

If this workflow fails, the product is not complete.

---

# 83. POWER-USER ACCEPTANCE TEST

An expert must additionally be able to:

* change machine type
* change CPU model
* attach disks
* attach ISOs
* configure networking
* configure port forwarding
* inspect generated command
* add custom QEMU arguments
* use serial console
* inspect QMP
* start GDB stub
* use direct kernel boot
* review QEMU logs

without the GUI fighting them.

---

# 84. HARDWARE ACCELERATION ACCEPTANCE TEST

On a normal compatible Windows 11 Intel or AMD machine:

```text
Create x86-64 VM
↓
Start VM
↓
Application selects WHPX
↓
QEMU confirms WHPX startup
↓
UI reports Hardware acceleration: Enabled
```

TCG must not be selected merely because implementation of WHPX was skipped.

On unsupported or cross-architecture configurations:

```text
TCG
```

must work with an explicit explanation that software emulation is slower.

---

# 85. WINDOWS-NATIVE ACCEPTANCE TEST

The application must correctly participate in:

* Windows 11 Snap Layouts
* dark/light switching
* high DPI
* Windows accent settings
* Narrator
* keyboard navigation
* native caption controls
* taskbar
* notifications
* application activation

A custom title bar must not break Snap Layouts.

---

# 86. DESIGN QUALITY GATE

Reject UI that resembles:

* a debug utility
* a WinForms app from 2008
* virt-manager copied verbatim
* generic Bootstrap
* an Electron dashboard
* a developer-only settings panel

Aim for:

> a first-party-quality Windows 11 virtual-machine application.

Whitespace, typography, density, animation, hierarchy, iconography and responsiveness all matter.

---

# 87. DO NOT FAKE FEATURES

Never:

* mark a button implemented when it does nothing
* return fake QEMU status
* show fake performance metrics
* hardcode screenshots
* claim WHPX while running TCG
* simulate snapshots without disk snapshots
* claim Secure Boot when firmware is not configured for it
* claim TPM when no TPM exists
* claim clipboard synchronization without working clipboard synchronization

Unimplemented functionality must either be implemented or removed from release UI.

---

# 88. NO PLACEHOLDER FINAL PRODUCT

The following are unacceptable as completion:

```text
TODO
stub
mock
placeholder
coming soon
not implemented
fake backend
dummy state
static VM screenshot
```

Tests must exercise real functionality.

---

# 89. IMPLEMENTATION ORDER

Execute roughly in this order, but continue automatically between stages:

## M0 — Foundation

* WinUI application
* architecture
* settings
* persistence
* logging
* packaging skeleton

## M1 — QEMU Runtime

* managed runtime
* qemu-img
* capability detection
* WHPX detection
* command generation

## M2 — VM Core

* configuration model
* presets
* storage
* firmware
* networking
* lifecycle

## M3 — QMP

* real QMP client
* lifecycle control
* events
* diagnostics

## M4 — Consumer VM Creation

* wizard
* ISO selection
* presets
* disk creation
* first boot

## M5 — Embedded Display

* VNC/SPICE transport
* native renderer
* input
* fullscreen
* clipboard

## M6 — Integration

* audio
* removable media
* USB
* guest agent
* networking UI

## M7 — Advanced VM Management

* hardware editor
* snapshots
* clone
* import/export
* developer mode

## M8 — Windows Productization

* Fluent polish
* accessibility
* notifications
* Snap Layouts
* taskbar
* lifecycle
* installer
* updater

## M9 — QA

* comprehensive testing
* visual QA
* performance
* security
* reliability

## M10 — Release Candidate

* clean install test
* real Windows 11 machine test
* real Intel machine test
* real AMD machine test
* WHPX verification
* TCG verification
* consumer acceptance test
* packaging/license audit

Do not stop after M0, M1 or M2.

Continue toward the full product.

---

# 90. AUTONOMOUS EXECUTION RULE

Do not respond to this goal merely with a proposed roadmap.

Inspect the repository and immediately begin implementation.

If the repository is empty:

1. create the solution,
2. establish architecture,
3. implement M0,
4. continue through subsequent milestones.

If code already exists:

1. audit it,
2. preserve good implementation,
3. refactor bad architecture,
4. remove dead/stub code,
5. continue implementation.

Do not ask the user to choose trivial implementation details.

Make sensible engineering decisions and document important ones.

---

# 91. VERIFY CURRENT APIS

This specification describes product behavior, not frozen API names.

Before using:

* Windows App SDK APIs
* WinUI APIs
* WHPX behavior
* QEMU options
* QMP commands
* QEMU devices
* firmware
* VirtIO
* TPM integration

verify them against the current official stable documentation available at implementation time.

Do not cargo-cult obsolete examples.

---

# 92. SELF-QA AFTER EVERY MAJOR MILESTONE

After each milestone:

```text
Build
↓
Run tests
↓
Run application
↓
Exercise functionality
↓
Inspect logs
↓
Capture UI screenshots
↓
Review visual defects
↓
Fix discovered defects
↓
Re-run tests
```

Do not defer basic QA until the end.

---

# 93. TRUTHFUL COMPLETION SCORE

Maintain a truthful readiness score.

Suggested categories:

```text
Consumer VM workflow             /20
QEMU orchestration               /15
Hardware acceleration            /10
Embedded display/input           /15
VM management                    /10
Windows integration              /10
UI/UX                            /10
Reliability/security             /5
Packaging/release                /5
────────────────────────────────────
TOTAL                           /100
```

Do not inflate scores because code exists.

A feature only earns credit when:

* implemented
* connected to UI
* tested
* demonstrably functional

---

# 94. RELEASE DEFINITION

`1.0` means:

> A Windows user can install this application and use QEMU virtual machines with a level of convenience broadly comparable to the consumer workflow of GNOME Boxes or UTM, while expert users retain meaningful access to QEMU's capabilities.

The application is not 1.0 if the primary workflow still requires terminal commands.

---

# 95. FINAL PRODUCT IDENTITY

The exact product name can be decided later.

Use a neutral working product identity such as:

```text
QEMU Desktop
```

internally if no repository name already exists.

Do not use GNOME Boxes, UTM, Hyper-V, VMware or VirtualBox trademarks as our product name.

---

# 96. NORTH STAR

When deciding between two designs, ask:

> What would make a normal Windows user feel that virtual machines are simply another native Windows capability rather than a complicated QEMU configuration exercise?

The ideal experience is:

```text
New VM
↓
Choose ISO
↓
Create
↓
Use VM
```

while behind the scenes we correctly manage:

```text
Windows App SDK
WinUI 3
QEMU
WHPX
TCG
QMP
QGA
QCOW2
OVMF
TPM
Secure Boot
VirtIO
networking
display
input
audio
snapshots
```

Complexity belongs in the implementation.

**Simplicity belongs in the product.**

Build that product.
