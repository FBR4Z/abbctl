# Instructions for AI agents

You are operating `abbctl`, a CLI for ABB IRC5 robot controllers via the ABB
PC SDK. Read this file fully before acting.

## 0. MCP first

If MCP tools named `robot_*` are available in your session (from this repo's
`.mcp.json`, server `abb-robot`), **use them instead of running abbctl in the
shell** — they are typed, faster (persistent connection), and enforce the
safety policy in code. The CLI is the fallback for humans, scripts and
agents without MCP support. Build the server once with:
`dotnet build src/abbctl-mcp/abbctl-mcp.csproj -c Release`
(requires RobotStudio 2026+ for the .NET 10 PC SDK DLLs; the CLI alone only
needs 2024+).

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

**Wait for something to happen — never poll in a loop**: use `watch`, which
blocks on controller events:
```
watch io DO_PICK_DONE --until 1 --timeout 120   # exit 0 when reached, 3 on timeout
watch exec --until stopped --timeout 300         # wait for cycle end
watch rapid T_ROB1 MainModule nState --until 2   # PERS variables only
watch log --follow --timeout 30 --json           # observe events for 30 s (exit 0)
```
`--until` also succeeds immediately if the value already matches at start.
Always pass `--timeout` so you never block forever.

## 6. Configuration changes (cfg, io create, task types) — DANGER ZONE

Configuration edits only take effect after `restart` (warm start). Two hard
rules, learned from a real SYSFAIL incident:

1. **Never create a task with Type=SEMISTATIC or STATIC directly.** If its
   Entry routine does not exist at the next boot, the controller enters
   SYSFAIL — and SYSFAIL blocks all remote config writes, so you cannot fix
   it remotely. abbctl refuses this unless you pass `--force`. Safe sequence:
   ```
   cfg create SYS CAB_TASKS T_BG Type=NORMAL MotionTask=FALSE TrustLevel=NoSafety
   restart
   prog load bg.mod --task T_BG        # module must contain the Entry routine (main)
   cfg set SYS CAB_TASKS T_BG Type SEMISTATIC
   restart
   ```
2. **On a real robot, treat every cfg change + restart as production-affecting**
   and get explicit user confirmation first. Before any config experiment,
   create a rollback point: `backup` (full system backup on the controller).

`io create <name> --type DO --access ALL` creates a device-less (memory)
signal writable by remote clients — safe, but still needs `restart`.

### Background (SEMISTATIC/STATIC) tasks: stopping and editing

- **The PC SDK cannot stop-and-hold a background task**: `task stop` is
  undone instantly by the system supervisor, and setting `Task.Enabled=false`
  does not stop it either (verified empirically). Only RobotStudio /
  FlexPendant's task panel can hold one stopped, via a channel the SDK does
  not expose.
- **To pause its work remotely**: gate the task's loop with a PERS variable
  and use `rapid set` (design pattern; no stop involved).
- **To edit its module WITHOUT a controller restart**: ask the user to stop
  the task from RobotStudio's task panel; then `prog load <mod> --task <t>
  --replace` → `prog reset --task <t>` → `prog start` (global start runs the
  panel-enabled tasks). A restart is only needed when creating a task or
  changing its Type.

## 7. Known behaviors

- `fs` paths are relative to the controller HOME directory; `fs ls` accepts
  glob patterns (`fs ls "backups/*"`).
- `io set` fails with an Access Level message when the signal's EIO
  configuration does not allow remote clients — that is controller
  configuration, not a tool bug.
- `prog start` requires Auto operating mode; `info` shows the current mode.
- RAPID values print in RAPID literal syntax (e.g. robtargets as
  `[[x,y,z],[q1..q4],...]`); `rapid set` accepts the same syntax.
