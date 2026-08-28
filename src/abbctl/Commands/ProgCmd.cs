using System;
using System.IO;
using System.Linq;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.RapidDomain;
using Task = ABB.Robotics.Controllers.RapidDomain.Task;

namespace AbbCtl.Commands
{
    internal static class ProgCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            if (args.Length == 0)
                throw new UsageException("prog requires a subcommand: tree | cat | save | load | unload | start | stop | reset | pp");

            string[] rest = args.Skip(1).ToArray();
            switch (args[0])
            {
                case "tree": return Tree(opts, rest);
                case "cat": return Cat(opts, rest);
                case "save": return Save(opts, rest);
                case "load": return Load(opts, rest);
                case "unload": return Unload(opts, rest);
                case "start": return Start(opts, rest);
                case "stop": return Stop(opts, rest);
                case "reset": return Reset(opts, rest);
                case "pp": return Pointer(opts, rest);
                default:
                    throw new UsageException("unknown prog subcommand '" + args[0] + "'");
            }
        }

        private static string TaskOption(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
                if ((args[i] == "--task" || args[i] == "-t") && i + 1 < args.Length)
                    return args[i + 1];
            return null;
        }

        private static Task ResolveTask(Controller c, string name)
        {
            Task[] tasks = c.Rapid.GetTasks();
            if (name == null)
            {
                if (tasks.Length == 1) return tasks[0];
                Task rob = tasks.FirstOrDefault(t => t.Name == "T_ROB1");
                if (rob != null) return rob;
                throw new UsageException("multiple tasks, specify one with --task: " +
                    string.Join(", ", tasks.Select(t => t.Name)));
            }
            Task found = tasks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found == null)
                throw new Exception("task '" + name + "' not found");
            return found;
        }

        public static int RunTasks(GlobalOptions opts, string[] args)
        {
            using (var s = Session.Open(opts))
            {
                var rows = new System.Collections.Generic.List<object>();
                foreach (Task t in s.Controller.Rapid.GetTasks())
                {
                    rows.Add(new JObj
                    {
                        { "name", t.Name },
                        { "enabled", t.Enabled },
                        { "type", t.TaskType.ToString() },
                        { "executionStatus", t.ExecutionStatus.ToString() }
                    });
                    if (!opts.Json)
                        Console.WriteLine("{0,-12} enabled={1,-6} {2,-10} {3}", t.Name, t.Enabled, t.TaskType, t.ExecutionStatus);
                }
                if (opts.Json) Json.Print(rows);
                return 0;
            }
        }

        private static int Tree(GlobalOptions opts, string[] args)
        {
            using (var s = Session.Open(opts))
            {
                var tasksOut = new System.Collections.Generic.List<object>();
                foreach (Task t in s.Controller.Rapid.GetTasks())
                {
                    var modsOut = new System.Collections.Generic.List<object>();
                    if (!opts.Json) Console.WriteLine(t.Name);
                    foreach (Module m in t.GetModules())
                    {
                        modsOut.Add(new JObj { { "name", m.Name } });
                        if (!opts.Json) Console.WriteLine("  " + m.Name);
                    }
                    tasksOut.Add(new JObj { { "task", t.Name }, { "modules", modsOut } });
                }
                if (opts.Json) Json.Print(tasksOut);
                return 0;
            }
        }

        /// <summary>
        /// Downloads one module's source via a temp file in the controller HOME
        /// directory (the PC SDK has no direct read of program-memory text).
        /// </summary>
        private static string FetchModuleText(Session s, Task task, string moduleName)
        {
            Module module = task.GetModule(moduleName);
            if (module == null)
                throw new Exception("module '" + moduleName + "' not found in task " + task.Name);

            // SaveToFile takes a *directory* and writes <ModuleName>.mod/.sys inside it.
            // FileSystem transfer/listing methods take paths relative to RemoteDirectory;
            // controller-side operations (SaveToFile) take the full controller path.
            var fs = s.Controller.FileSystem;
            string tmpDir = "abbctl_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string localPath = Path.Combine(Path.GetTempPath(), "abbctl_" + module.Name + ".mod");

            fs.CreateDirectory(tmpDir);
            try
            {
                module.SaveToFile(fs.RemoteDirectory + "/" + tmpDir);
                var file = fs.GetFilesAndDirectories(tmpDir + "/*")
                    .OfType<ABB.Robotics.Controllers.FileSystemDomain.ControllerFileInfo>()
                    .FirstOrDefault();
                if (file == null)
                    throw new Exception("controller did not produce a file for module '" + module.Name + "'");
                fs.GetFile(tmpDir + "/" + file.Name, localPath, true);
                return File.ReadAllText(localPath);
            }
            finally
            {
                try { fs.RemoveDirectory(tmpDir, true); } catch { }
                try { File.Delete(localPath); } catch { }
            }
        }

        private static int Cat(GlobalOptions opts, string[] args)
        {
            string moduleName = args.FirstOrDefault(a => !a.StartsWith("-") && a != TaskOption(args));
            if (moduleName == null) throw new UsageException("prog cat <module> [--task <t>]");

            using (var s = Session.Open(opts))
            {
                Task task = ResolveTask(s.Controller, TaskOption(args));
                string text = FetchModuleText(s, task, moduleName);
                if (opts.Json)
                    Json.Print(new JObj { { "task", task.Name }, { "module", moduleName }, { "source", text } });
                else
                    Console.Write(text);
                return 0;
            }
        }

        private static int Save(GlobalOptions opts, string[] args)
        {
            string dir = args.FirstOrDefault(a => !a.StartsWith("-") && a != TaskOption(args));
            if (dir == null) throw new UsageException("prog save <localdir> [--task <t>]");
            Directory.CreateDirectory(dir);

            using (var s = Session.Open(opts))
            {
                Task task = ResolveTask(s.Controller, TaskOption(args));
                var saved = new System.Collections.Generic.List<object>();
                foreach (Module m in task.GetModules())
                {
                    string text = FetchModuleText(s, task, m.Name);
                    string ext = m.IsSystem ? ".sys" : ".mod";
                    string path = Path.Combine(dir, m.Name + ext);
                    File.WriteAllText(path, text);
                    saved.Add(path);
                    if (!opts.Json) Console.WriteLine("saved " + path);
                }
                if (opts.Json) Json.Print(new JObj { { "task", task.Name }, { "files", saved } });
                return 0;
            }
        }

        private static int Load(GlobalOptions opts, string[] args)
        {
            string file = args.FirstOrDefault(a => !a.StartsWith("-") && a != TaskOption(args));
            if (file == null) throw new UsageException("prog load <file.mod|.sys|.pgf> [--task <t>] [--replace]");
            if (!File.Exists(file)) throw new Exception("file not found: " + file);
            bool replace = args.Contains("--replace");
            var mode = replace ? RapidLoadMode.Replace : RapidLoadMode.Add;

            using (var s = Session.Open(opts))
            {
                var c = s.Controller;
                Task task = ResolveTask(c, TaskOption(args));

                if (c.Rapid.ExecutionStatus == ExecutionStatus.Running)
                    throw new Exception("RAPID execution is running; run 'abbctl prog stop' first (module load requires stopped execution)");

                var fs = c.FileSystem;
                string remoteName = "abbctl_" + Path.GetFileName(file);
                // PutFile/RemoveFile take HOME-relative paths; Load*FromFile runs on the
                // controller and needs the full controller path.
                string remotePath = fs.RemoteDirectory + "/" + remoteName;
                bool isProgram = Path.GetExtension(file).Equals(".pgf", StringComparison.OrdinalIgnoreCase);

                fs.PutFile(file, remoteName, true);
                try
                {
                    bool ok;
                    using (Mastership.Request(c))
                    {
                        ok = isProgram
                            ? task.LoadProgramFromFile(remotePath, mode)
                            : task.LoadModuleFromFile(remotePath, mode);
                    }
                    if (!ok)
                        throw new Exception("controller rejected the load (check RAPID syntax and event log: 'abbctl log')");
                }
                finally
                {
                    try { fs.RemoveFile(remoteName); } catch { }
                }

                if (opts.Json)
                    Json.Print(new JObj { { "task", task.Name }, { "loaded", file }, { "mode", mode.ToString() } });
                else
                    Console.WriteLine("loaded " + file + " into " + task.Name + " (" + mode + ")");
                return 0;
            }
        }

        private static int Unload(GlobalOptions opts, string[] args)
        {
            string moduleName = args.FirstOrDefault(a => !a.StartsWith("-") && a != TaskOption(args));
            if (moduleName == null) throw new UsageException("prog unload <module> [--task <t>]");

            using (var s = Session.Open(opts))
            {
                Task task = ResolveTask(s.Controller, TaskOption(args));
                Module module = task.GetModule(moduleName);
                if (module == null) throw new Exception("module '" + moduleName + "' not found");

                using (Mastership.Request(s.Controller))
                    module.Delete();

                if (opts.Json) Json.Print(new JObj { { "task", task.Name }, { "unloaded", moduleName } });
                else Console.WriteLine("unloaded " + moduleName);
                return 0;
            }
        }

        private static int Start(GlobalOptions opts, string[] args)
        {
            var cycle = ExecutionCycle.Forever;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--cycle" && i + 1 < args.Length && args[i + 1] == "once")
                    cycle = ExecutionCycle.Once;

            using (var s = Session.Open(opts))
            {
                var c = s.Controller;
                if (c.OperatingMode != ControllerOperatingMode.Auto)
                    throw new Exception("controller is in " + c.OperatingMode + " mode; remote start requires Auto mode");

                StartResult result;
                using (Mastership.Request(c))
                    result = c.Rapid.Start(RegainMode.Continue, ExecutionMode.Continuous, cycle, StartCheck.CallChain);

                if (result != StartResult.Ok)
                    throw new Exception("start failed: " + result);

                if (opts.Json) Json.Print(new JObj { { "started", true }, { "cycle", cycle.ToString() } });
                else Console.WriteLine("started (" + cycle + ")");
                return 0;
            }
        }

        private static int Stop(GlobalOptions opts, string[] args)
        {
            using (var s = Session.Open(opts))
            {
                using (Mastership.Request(s.Controller))
                    s.Controller.Rapid.Stop(StopMode.Immediate);

                if (opts.Json) Json.Print(new JObj { { "stopped", true } });
                else Console.WriteLine("stopped");
                return 0;
            }
        }

        private static int Reset(GlobalOptions opts, string[] args)
        {
            using (var s = Session.Open(opts))
            {
                Task task = ResolveTask(s.Controller, TaskOption(args));
                using (Mastership.Request(s.Controller))
                    task.ResetProgramPointer();

                if (opts.Json) Json.Print(new JObj { { "task", task.Name }, { "programPointer", "main" } });
                else Console.WriteLine("program pointer reset to main (" + task.Name + ")");
                return 0;
            }
        }

        private static int Pointer(GlobalOptions opts, string[] args)
        {
            using (var s = Session.Open(opts))
            {
                var rows = new System.Collections.Generic.List<object>();
                foreach (Task t in s.Controller.Rapid.GetTasks())
                {
                    string pp = DescribePosition(() => t.ProgramPointer);
                    string mp = DescribePosition(() => t.MotionPointer);
                    rows.Add(new JObj { { "task", t.Name }, { "programPointer", pp }, { "motionPointer", mp } });
                    if (!opts.Json)
                        Console.WriteLine("{0,-12} pp: {1,-40} mp: {2}", t.Name, pp, mp);
                }
                if (opts.Json) Json.Print(rows);
                return 0;
            }
        }

        private static string DescribePosition(Func<ProgramPosition> get)
        {
            try
            {
                ProgramPosition pos = get();
                if (pos == null) return "(not set)";
                return pos.Module + "/" + pos.Routine + ":" + pos.Range.Begin.Row;
            }
            catch
            {
                return "(not set)";
            }
        }
    }
}
