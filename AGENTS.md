# Instructions for AI agents

You are operating `abbctl`, a CLI for ABB IRC5 robot controllers via the ABB
PC SDK. Read this file fully before acting.

## 1. Verify the environment (do this first)

Run `./scripts/check-env.ps1`. It checks, and tells you how to fix:

1. **Windows** — the PC SDK is Windows-only; this project cannot build or run
   on Linux/macOS.
2. **PC SDK DLLs** — from RobotStudio 2024+ or a standalone PC SDK install.
   If missing, ask the user to install RobotStudio, or set `ABB_PCSDK_DIR` to
   a folder containing the net48 `ABB.Robotics.Controllers.PC.dll`.
3. **.NET SDK** — if `dotnet` is not on PATH, try
   `"C:\Program Files\dotnet\dotnet.exe"` directly; if absent, install with
   `winget install Microsoft.DotNet.SDK.10 --silent --accept-package-agreements --accept-source-agreements`.

## 2. Build

```powershell
dotnet build src/abbctl/abbctl.csproj -c Release
```

Binary: `src/abbctl/bin/Release/abbctl.exe`. A clean build takes seconds. If
the build errors with "PC SDK not found", resolve step 1.2 above.

## 3. Operating rules

- **Always prefer `--json`** — machine-readable, stable field order. Human
  format may change.
- Exit codes: `0` ok, `1` controller/runtime error (stderr has the reason),
  `2` usage error. Set `ABBCTL_DEBUG=1` for stack traces.
- Target selection: with a single controller on the network, no flag is
  needed. Otherwise `-c <ip|name>` or the `ABBCTL_CONTROLLER` env var.
- A virtual controller only appears in `scan` after the user starts the
  station in RobotStudio. If `scan` is empty, ask the user to start it —
  you cannot start it yourself.

## 4. Safety — read before writing to a controller

- Check `abbctl info --json` first. `"virtual": true` means a RobotStudio
  simulation — experimentation is safe. `"virtual": false` is a **real
  robot**: never `prog start`, `prog stop`, `prog load`, `io set` or
  `rapid set` on it without explicit user confirmation for that specific
  action. A started program moves a physical machine.
- The program-edit cycle (`prog load --replace`) requires stopped execution;
  `abbctl` enforces this and tells you when to run `prog stop` first.
- Before replacing a module, keep a copy of the original (`prog save`) so you
  can roll back by loading it again.
- Loading a module with RAPID syntax errors fails; the original module has
  already been removed at that point (Replace mode). Recover by loading the
  saved copy. Always validate edits carefully before `prog load`.

## 5. Typical workflows

**Inspect**: `scan` → `info` → `prog tree` → `prog cat <module>` →
`prog pp` (execution position) → `log -n 20` (recent controller events).

**Edit the program**:
```
prog stop
prog save <dir>          # originals = rollback copies
<edit the .mod file locally>
prog load <file> --replace
prog reset
prog start
```

**Tune without stopping**: declare `PERS` variables in RAPID, then
`rapid set <task> <module> <symbol> <value>` while the program runs.

**Diagnose failures**: `log -n 20 --json` right after any controller error —
the event log contains the controller-side reason (syntax errors, safety
rejections, etc.).

## 6. Known behaviors

- `fs` paths are relative to the controller HOME directory; `fs ls` accepts
  glob patterns (`fs ls "backups/*"`).
- `io set` fails with an Access Level message when the signal's EIO
  configuration does not allow remote clients — that is controller
  configuration, not a tool bug.
- `prog start` requires Auto operating mode; `info` shows the current mode.
- RAPID values print in RAPID literal syntax (e.g. robtargets as
  `[[x,y,z],[q1..q4],...]`); `rapid set` accepts the same syntax.
