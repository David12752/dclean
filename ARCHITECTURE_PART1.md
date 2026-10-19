# DClean — PC Cleaning & Resource Management Utility
## Part 1: Tech Stack Decision Matrix & Architecture

**Version:** 1.0 | **Date:** 2026-03-31

---

## 1.1 Language Candidates

| Criterion | C++ (Win32) | Rust | C# (.NET 8) |
|---|---|---|---|
| Win32/NTAPI interop | Native — zero overhead | Good via `windows-rs` crate | Excellent via `P/Invoke` + `CsWin32` source-gen |
| Memory safety | Manual — UAF/overflow risk | Best-in-class ownership model | GC-managed; no manual memory bugs |
| Development velocity | Slow; large boilerplate | Moderate | Highest |
| RTL/Hebrew UI ecosystem | MFC archaic; WTL unmaintained | No mature native GUI; `egui` has RTL gaps | WPF first-class `FlowDirection=RightToLeft` |
| Async I/O for disk scans | C++20 coroutines: verbose | Tokio: excellent | `Task`/`async-await`: battle-tested |
| Windows Service ecosystem | Excellent | `windows-service` crate: production-ready | `System.ServiceProcess`: 20+ years stable |

**Decision: C# 12 / .NET 8**

Rationale: The Win32 surface needed (file enumeration, process management, memory stats) is fully covered by `CsWin32` source-generator, which auto-generates type-safe wrappers from Windows metadata — eliminating the primary argument for C++. NativeAOT is available if startup time becomes critical.

> **Why not Rust?** The `windows-rs` crate still has gaps in COM automation (`IShellLink`, `IStorageItem`) that `CsWin32` covers exhaustively. The productivity cost in a breadth-heavy Win32 project is unjustified.

---

## 1.2 UI Framework Candidates

| Criterion | WPF (.NET 8) | WinUI 3 | Avalonia |
|---|---|---|---|
| RTL / Hebrew | **Best** — `FlowDirection="RightToLeft"` cascades entire visual tree | Per-element; open bugs in `NavigationView` mirroring | Known gaps in complex RTL script shaping |
| Modern XAML UI | Data binding, animations, styles, templates | Fluent Design / MICA material | Modern but Windows features need platform calls |
| Win32 HWND interop | `HwndSource`, `WindowInteropHelper` | `AppWindow`-based; HWND accessible | Less direct |
| UI Automation (Accessibility) | Full UIA tree | Full UIA tree | Partial |
| Deployment | Simple xcopy / MSIX | Requires Windows App SDK runtime | Simple |
| Maturity | 17+ years | 3+ years; still maturing | 5+ years |

**Decision: WPF on .NET 8**

The single decisive factor: `FlowDirection="RightToLeft"` at the `Window` root causes the **entire** XAML visual tree — panels, text, icons, scroll bars, tab order — to mirror correctly for Hebrew with zero per-control configuration. WinUI 3 requires per-element annotation and has several open RTL issues. For a Hebrew-only application, WPF's cascading RTL is non-negotiable.

---

## 1.3 Final Stack

```
Language:        C# 12 / .NET 8
UI Framework:    WPF (.NET 8, FlowDirection=RightToLeft at Window root)
Win32 Interop:   CsWin32 (source generator) + manual P/Invoke for NTAPI
Service Host:    .NET 8 Worker Service (Windows Service mode)
IPC:             Named Pipes (System.IO.Pipes) with a length-prefixed protobuf protocol
Serialization:   Google.Protobuf (fast, schema-versioned, no reflection overhead)
Logging:         Microsoft.Extensions.Logging + ETW via EventSource
Packaging:       MSIX (via WiX 5 or the MSIX Packaging Tool)
Unit Tests:      xUnit + Moq
```

---

## 1.4 High-Level System Architecture

```
┌──────────────────────────────────────────────────┐
│              DClean.UI.exe (Standard User)        │
│  WPF / .NET 8 / FlowDirection=RightToLeft        │
│  ┌────────────┐  ┌──────────────┐  ┌──────────┐ │
│  │ Dashboard  │  │ CleaningView │  │ ProcView │ │
│  └─────┬──────┘  └──────┬───────┘  └────┬─────┘ │
│        └─────────────────┴───────────────┘       │
│                    ViewModel Layer                │
│              (MVVM / INotifyPropertyChanged)      │
│                    IPC Client                     │
│             (NamedPipeClientStream)               │
└─────────────────────┬────────────────────────────┘
                      │ Named Pipe (protobuf)
                      │ \\\\.\\pipe\\DCleanService
┌─────────────────────▼────────────────────────────┐
│         DClean.Service.exe (LOCAL SYSTEM)         │
│         Windows Service / .NET 8 Worker           │
│  ┌─────────────────────────────────────────────┐ │
│  │              Safety Gate Layer              │ │
│  │  PathValidator | ProcessBlacklist | DryRun  │ │
│  └──────────────┬──────────────────────────────┘ │
│  ┌──────────────▼──────────────────────────────┐ │
│  │            Cleaning Engine                  │ │
│  │  FileScanner | LockedFileHandler            │ │
│  │  RegistryScanner | BrowserCacheLocator      │ │
│  └──────────────┬──────────────────────────────┘ │
│  ┌──────────────▼──────────────────────────────┐ │
│  │         Memory & Process Manager            │ │
│  │  ProcessEnumerator | WorkingSetTrimmer       │ │
│  │  ProcessTerminator (WM_CLOSE first)         │ │
│  └─────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────┘
```

**Why two processes?**

The UI **never** directly calls `DeleteFile` or `TerminateProcess`. All destructive commands are serialized as protobuf messages to the service. The service re-validates every path against the Safety Gate before acting. A compromised or buggy UI cannot brick the OS — the service is the last line of defense.

---

## 1.5 IPC Protocol Design

The named pipe carries length-prefixed protobuf messages. Defining a `command.proto`:

```protobuf
syntax = "proto3";

enum OperationMode { DRY_RUN = 0; EXECUTE = 1; }

message ScanRequest  { OperationMode mode = 1; repeated string targets = 2; }
message DeleteRequest { OperationMode mode = 1; string path = 2; }
message KillRequest  { OperationMode mode = 1; uint32 pid = 2; }

message ProgressEvent {
  string path        = 1;
  int64  bytes_freed = 2;
  bool   was_skipped = 3; // safety gate blocked it
  string skip_reason = 4;
}
```

`OperationMode.DRY_RUN` is the **default and initial state**. The service refuses to switch to `EXECUTE` unless the UI sends a signed confirmation token generated after the user clicks a two-step confirm dialog. This prevents accidental state transitions.

---

## 1.6 UAC & Privilege Separation

- `DClean.UI.exe` manifest: `requestedExecutionLevel level="asInvoker"` — runs as standard user.
- `DClean.Service.exe`: installed as a Windows Service running under `LocalSystem` with `SeBackupPrivilege`, `SeRestorePrivilege`, `SeDebugPrivilege` enabled only when needed (not persistent in the token).
- Privilege activation uses `AdjustTokenPrivileges` immediately before the operation and **revoked immediately after**.
- The service's pipe ACL only grants `FILE_READ_DATA | FILE_WRITE_DATA` to the **interactive user SID**, not `Everyone`.

```csharp
// Pipe security — only the current interactive user may connect
var security = new PipeSecurity();
security.AddAccessRule(new PipeAccessRule(
    WindowsIdentity.GetCurrent().User!,
    PipeAccessRights.ReadWrite,
    AccessControlType.Allow));
var pipe = NamedPipeServerStreamAcl.Create(
    "DCleanService", PipeDirection.InOut,
    maxNumberOfServerInstances: 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
    inBufferSize: 4096, outBufferSize: 4096,
    pipeSecurity: security);
```
