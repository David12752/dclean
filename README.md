# DClean — PC Cleaning & Resource Management Utility
### Architecture & Project Plan — Master Index

---

## Document Map

| File | Contents |
|---|---|
| `ARCHITECTURE_PART1.md` | Tech Stack Decision Matrix, Final Stack, System Architecture Diagram, IPC Protocol, UAC & Privilege Design |
| `ARCHITECTURE_PART2_PHASE1.md` | Phase 1 — Solution Structure, Manifests, CsWin32 Setup, Dry-Run State Machine, Service Registration, ETW Logging |
| `ARCHITECTURE_PART2_PHASE2.md` | Phase 2 — Safety Gate (PathValidator), Browser Cache Locator, FileScanner, SafeDeleter, Locked-File Handling |
| `ARCHITECTURE_PART2_PHASE3.md` | Phase 3 — Process Blacklist, ProcessEnumerator, WorkingSet Trimmer, Safe Terminator, PID Recycling Defense, RAM Monitor |
| `ARCHITECTURE_PART2_PHASE4.md` | Phase 4 — RTL Window Config, WPF Theme, MVVM ViewModel, Hebrew UI XAML, Async Threading Patterns |
| `ARCHITECTURE_PART2_PHASE5.md` | Phase 5 — Unit/Integration/Fuzz Tests, VM Test Matrix, MSIX Packaging, Build Pipeline, Installation Script |
| `ARCHITECTURE_APPENDICES.md` | Appendix A (Safety Manifests), B (Win32 API Reference), C (Edge Cases), D (Glossary) |

---

## Executive Summary

**DClean** is a two-process, privilege-separated Windows utility built on:

- **Language:** C# 12 / .NET 8
- **UI:** WPF with `FlowDirection="RightToLeft"` (full Hebrew RTL cascade)
- **Service:** .NET 8 Worker Service registered as a Windows Service (`LocalSystem`)
- **IPC:** Named Pipes with protobuf serialization
- **Win32 Interop:** CsWin32 source generator

The **Zero-Bricking Policy** is enforced through:
1. A hardcoded `PathValidator` (allowlist + forbidden-prefix + forbidden-extension + symlink resolution)
2. A `DryRunGuard` state machine (default safe; requires 2-step token confirmation to enter Execute mode)
3. A compiled-in `ProcessBlacklist` covering all OS-critical processes
4. The UI **never** calls `DeleteFile` or `TerminateProcess` directly — all destructive commands pass through the service's Safety Gate

---

## Development Phase Order

```
Phase 1  →  Phase 2  →  Phase 3  →  Phase 4  →  Phase 5
Foundation  Cleaning     Memory       Hebrew UI    Testing &
& Safety    Engine       & Process                 Deployment
             Manager
```

**Estimated effort (single senior developer):**

| Phase | Estimated Duration |
|---|---|
| Phase 1 — Foundation & Security | 1 week |
| Phase 2 — Cleaning Engine | 2 weeks |
| Phase 3 — Memory & Process Manager | 1.5 weeks |
| Phase 4 — Hebrew UI | 1.5 weeks |
| Phase 5 — Testing & Deployment | 1 week |
| **Total** | **~7 weeks** |

---

## Quick-Reference: Critical Safety Rules

1. **`PathValidator.Validate()` is called twice per file:** once in the scanner, once in the deleter. This is intentional and not a bug.
2. **`DryRunGuard.IsExecuteMode` defaults to `false`** and can only be set to `true` via a two-step token exchange. It is always reset after a run.
3. **`ProcessBlacklist.CriticalProcessNames` is a compile-time constant.** It is never loaded from disk.
4. **Always call `FileInfo.ResolveLinkTarget(returnFinalTarget: true)` before any path prefix check.** Skipping this enables symlink-traversal attacks.
5. **Always check `FileAttributes.ReparsePoint` during directory traversal and skip.** Do not enter junction directories.
6. **`MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` is the only acceptable action for locked files.** Never retry deletion in a loop.
7. **Use `WM_CLOSE` first; wait 5 seconds; only then use `TerminateProcess` if explicitly re-confirmed by user.**
8. **Verify process name after `OpenProcess` and before `TerminateProcess` to defend against PID recycling.**
9. **The named pipe ACL allows only the current interactive user SID.** `Everyone` is never added.
10. **Privileges (`SeDebugPrivilege`, etc.) are enabled immediately before use and revoked immediately after** using `AdjustTokenPrivileges`.
