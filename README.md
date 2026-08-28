# abbctl

Command-line tool for ABB IRC5 robot controllers, built on the ABB **PC SDK**.
Works against real controllers (requires the **PC Interface** option, 616-1) and
against **virtual controllers** in RobotStudio (no option required).

Designed to be driven both by humans and by AI coding agents (Claude Code,
Codex, etc.): every command has a `--json` output mode, predictable exit codes,
and the repository ships with [AGENTS.md](AGENTS.md) describing how an agent
should verify the environment, build and operate the tool.

```
abbctl scan                        # discover controllers on the network
abbctl info                        # state, operating mode, RobotWare version
abbctl prog tree                   # tasks > modules
abbctl prog cat MainModule         # print RAPID source of a module
abbctl prog save ./backup          # download all modules of a task
abbctl prog load Main.mod --replace
abbctl prog stop / start / reset / pp
abbctl rapid get T_ROB1 MainModule nCount
abbctl rapid set T_ROB1 MainModule nCount 10
abbctl io list / get / set
abbctl pos [--joints]              # current TCP / joint position
abbctl speed 25                    # set the controller speed ratio (%)
abbctl watch io DO_PICK_DONE --until 1 --timeout 60   # block on events, no polling
abbctl watch exec --until stopped  # wait for the program to stop
abbctl watch log --follow          # stream controller events live
abbctl log -n 20                   # controller event log
abbctl fs ls / get / put           # controller file system (HOME)
abbctl backup                      # full system backup on the controller disk
```

Run `abbctl --help` for the full reference.

## Requirements

- **Windows** (the PC SDK is Windows-only)
- **RobotStudio 2024 or newer** (any edition — its installation provides the
  PC SDK DLLs), or a standalone ABB PC SDK installation. The build probes, in
  order:
  1. the `ABB_PCSDK_DIR` environment variable (a folder containing
     `ABB.Robotics.Controllers.PC.dll`, net48 build)
  2. `C:\Program Files (x86)\ABB\SDK\PCSDK\net48`
  3. `C:\Program Files (x86)\ABB\RobotStudio 2026\Bin-net48`
  4. `C:\Program Files (x86)\ABB\RobotStudio 2025\Bin`
  5. `C:\Program Files (x86)\ABB\RobotStudio 2024\Bin`
- **.NET SDK** (any recent version; the project targets .NET Framework 4.8,
  which is preinstalled on Windows — the SDK is only needed to build):
  `winget install Microsoft.DotNet.SDK.10`

ABB's DLLs are **not** included in this repository (they are not
redistributable); they are referenced from your local installation and copied
next to `abbctl.exe` at build time.

## Build

```powershell
./scripts/check-env.ps1        # verifies prerequisites and reports what is missing
dotnet build src/abbctl/abbctl.csproj -c Release
# result: src/abbctl/bin/Release/abbctl.exe
```

Optionally add the output folder to your `PATH`.

## Quick start with a virtual controller

1. Open a station in RobotStudio and start its virtual controller.
2. `abbctl scan` — the controller appears (no PC Interface option needed for
   virtual controllers).
3. If it is the only controller on the network, every other command finds it
   automatically. Otherwise select it with `-c <ip|name>` or set the
   `ABBCTL_CONTROLLER` environment variable.

## Editing the robot program from the CLI

The PC SDK edits programs at module granularity. The cycle is:

```powershell
abbctl prog stop                       # module replace requires stopped execution
abbctl prog save .\work                # download current modules
# edit .\work\MainModule.mod with any editor (or let an AI agent edit it)
abbctl prog load .\work\MainModule.mod --replace
abbctl prog reset                      # program pointer back to main
abbctl prog start
```

No controller restart is needed — program loads are a program-memory
operation and take effect immediately.

For runtime parameter changes without stopping the program, use RAPID
persistents and `abbctl rapid set`.

## Waiting on the robot (events, not polling)

`abbctl watch` subscribes to controller events and blocks until something
happens — the natural building block for scripts and AI agents:

- `watch io <signal>` / `watch rapid <task> <module> <symbol>` (PERS only) /
  `watch exec` / `watch state` — exit 0 on the first change, or when
  `--until <value>` is reached (also if the value already matches at start).
- `--follow` streams changes indefinitely (NDJSON with `--json`).
- `--timeout <s>` bounds the wait: exit 3 if the awaited change never came.
- `watch log` streams new event log messages as they are written.

## Notes and limitations

- **Motion cannot be commanded directly** through the PC SDK; motion always
  comes from the RAPID program. Write robtargets to persistents that your
  RAPID code consumes, or use EGM (out of scope for this tool).
- **Remote start and writes require Auto mode.** In Manual mode the
  FlexPendant must grant access.
- **Mastership**: write operations briefly acquire mastership and release it
  immediately, so coexistence with RobotStudio online mode works — just don't
  hold Write Access in RobotStudio while abbctl writes.
- **I/O writes** require the signal's *Access Level* to include remote
  clients (`ALL`) in the EIO configuration; otherwise the controller rejects
  the write.
- On a real controller the network scan finds controllers in the same subnet;
  for other subnets pass the IP with `-c`.

## Exit codes

`0` success · `1` runtime/controller error (message on stderr) · `2` usage
error.

## License

MIT — see [LICENSE](LICENSE). Not affiliated with ABB. RobotStudio and the PC
SDK are ABB products; consult ABB's license terms for their components.
