using System;
using System.Collections.Generic;
using System.Linq;
using AbbCtl.Commands;

namespace AbbCtl
{
    internal static class Program
    {
        private const string Usage = @"abbctl - CLI for ABB IRC5 controllers (PC SDK / PC Interface)

Usage: abbctl [global options] <command> [args]

Commands:
  scan                                   List controllers on the network
  info                                   Controller state, mode, system info
  tasks                                  List RAPID tasks
  task list                              Tasks with type (Normal/Static/SemiStatic)
  task start <name> | task stop <name>   Start/stop one task individually
  io create <name> --type DO [...]       Create an I/O signal (needs restart)
  cfg list [domain [type]]               Browse the configuration database
  cfg get <dom> <type> <inst> [attr]     Read configuration
  cfg set <dom> <type> <inst> <attr> <v> Write configuration (needs restart)
  cfg create <dom> <type> <name> [A=V..] Create a config instance (needs restart)
  cfg delete <dom> <type> <inst>         Delete a config instance (needs restart)
  cfg load <file.cfg> [--mode add]       Load a .cfg file (needs restart)
  restart [--no-wait]                    Warm start and wait for reconnection
  io list [--filter <substr>]            List I/O signals
  io get <signal>                        Read a signal
  io set <signal> <value>                Write a signal
  rapid get <task> <module> <symbol>     Read a RAPID variable/persistent
  rapid set <task> <module> <symbol> <value>   Write a RAPID variable/persistent
  prog tree                              Tasks > modules > routines
  prog cat <module> [--task <t>]         Print module source code
  prog save <localdir> [--task <t>]      Download all modules of a task
  prog load <file> [--task <t>] [--replace]    Load a .mod/.sys module (or .pgf program)
  prog unload <module> [--task <t>]      Remove a module from program memory
  prog start [--cycle once|forever]      Start RAPID execution (auto mode)
  prog stop                              Stop RAPID execution
  prog reset [--task <t>]                Reset program pointer to main
  prog pp                                Show program/motion pointer
  pos [--joints]                         Current robot position
  speed [0-100]                          Read or set the speed ratio (%)
  watch io <signal>                      Block until a signal changes
  watch rapid <task> <module> <symbol>   Block until a PERS value changes
  watch exec                             Block until execution status changes
  watch state                            Block until controller state/mode changes
  watch log                              Stream new event log messages
    watch options: [--until <value>] [--follow] [--timeout <s>]
    exit 0 = change/until reached; exit 3 = timeout
  log [-n <count>]                       Recent event log messages
  fs ls [path]                           List controller files
  fs get <remote> [local]                Download a file
  fs put <local> [remote]                Upload a file
  backup [name]                          Create a system backup on the controller

Global options:
  -c, --controller <ip|name|guid>   Target controller (default: $ABBCTL_CONTROLLER,
                                    or the only controller found on the network)
  -u, --user <name>                 Controller login (default: Default User)
  -p, --password <pwd>              Controller password (default: robotics)
      --json                        Machine-readable JSON output
  -h, --help                        Show this help

Exit codes: 0 ok, 1 runtime error, 2 usage error.";

        internal static int Main(string[] args)
        {
            var opts = new GlobalOptions();
            var rest = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-c":
                    case "--controller":
                        if (++i >= args.Length) return UsageError("missing value for " + a);
                        opts.Controller = args[i];
                        break;
                    case "-u":
                    case "--user":
                        if (++i >= args.Length) return UsageError("missing value for " + a);
                        opts.User = args[i];
                        break;
                    case "-p":
                    case "--password":
                        if (++i >= args.Length) return UsageError("missing value for " + a);
                        opts.Password = args[i];
                        break;
                    case "--json":
                        opts.Json = true;
                        break;
                    case "-h":
                    case "--help":
                        Console.WriteLine(Usage);
                        return 0;
                    default:
                        rest.Add(a);
                        break;
                }
            }

            if (opts.Controller == null)
                opts.Controller = Environment.GetEnvironmentVariable("ABBCTL_CONTROLLER");

            if (rest.Count == 0)
            {
                Console.WriteLine(Usage);
                return 2;
            }

            string cmd = rest[0];
            string[] cmdArgs = rest.Skip(1).ToArray();

            try
            {
                switch (cmd)
                {
                    case "scan": return ScanCmd.Run(opts, cmdArgs);
                    case "info": return InfoCmd.RunInfo(opts, cmdArgs);
                    case "pos": return InfoCmd.RunPos(opts, cmdArgs);
                    case "speed": return SpeedCmd.Run(opts, cmdArgs);
                    case "watch": return WatchCmd.Run(opts, cmdArgs);
                    case "tasks": return ProgCmd.RunTasks(opts, cmdArgs);
                    case "task": return TaskCmd.Run(opts, cmdArgs);
                    case "cfg": return CfgCmd.Run(opts, cmdArgs);
                    case "restart": return RestartCmd.Run(opts, cmdArgs);
                    case "io": return IoCmd.Run(opts, cmdArgs);
                    case "rapid": return RapidCmd.Run(opts, cmdArgs);
                    case "prog": return ProgCmd.Run(opts, cmdArgs);
                    case "log": return LogCmd.Run(opts, cmdArgs);
                    case "fs": return FsCmd.Run(opts, cmdArgs);
                    case "backup": return BackupCmd.Run(opts, cmdArgs);
                    default:
                        return UsageError("unknown command '" + cmd + "'. Run 'abbctl --help'.");
                }
            }
            catch (UsageException ux)
            {
                return UsageError(ux.Message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                if (Environment.GetEnvironmentVariable("ABBCTL_DEBUG") == "1")
                    Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static int UsageError(string msg)
        {
            Console.Error.WriteLine("usage error: " + msg);
            return 2;
        }
    }

    internal sealed class GlobalOptions
    {
        public string Controller;
        public string User = "Default User";
        public string Password = "robotics";
        public bool Json;
    }

    internal sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }
}
