using System;
using System.Linq;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.RapidDomain;
using Task = ABB.Robotics.Controllers.RapidDomain.Task;

namespace AbbCtl.Commands
{
    internal static class TaskCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            if (args.Length == 0)
                throw new UsageException("task requires a subcommand: list | start | stop");

            string[] rest = args.Skip(1).ToArray();
            switch (args[0])
            {
                case "list": return List(opts);
                case "start": return StartStop(opts, rest, true);
                case "stop": return StartStop(opts, rest, false);
                default:
                    throw new UsageException("unknown task subcommand '" + args[0] + "'");
            }
        }

        private static int List(GlobalOptions opts)
        {
            using (var s = Session.Open(opts))
            {
                var rows = new System.Collections.Generic.List<object>();
                foreach (Task t in s.Controller.Rapid.GetTasks())
                {
                    rows.Add(new JObj
                    {
                        { "name", t.Name },
                        { "type", t.TaskType.ToString() },
                        { "executionType", t.ExecutionType.ToString() },
                        { "enabled", t.Enabled },
                        { "motion", t.Motion },
                        { "executionStatus", t.ExecutionStatus.ToString() }
                    });
                    if (!opts.Json)
                        Console.WriteLine("{0,-14} {1,-11} enabled={2,-6} motion={3,-6} {4}",
                            t.Name, t.TaskType, t.Enabled, t.Motion, t.ExecutionStatus);
                }
                if (opts.Json) Json.Print(rows);
                return 0;
            }
        }

        private static int StartStop(GlobalOptions opts, string[] args, bool start)
        {
            if (args.Length < 1)
                throw new UsageException("task " + (start ? "start" : "stop") + " <name>");

            using (var s = Session.Open(opts))
            {
                Task t = s.Controller.Rapid.GetTasks()
                    .FirstOrDefault(x => string.Equals(x.Name, args[0], StringComparison.OrdinalIgnoreCase));
                if (t == null) throw new Exception("task '" + args[0] + "' not found");

                using (Mastership.Request(s.Controller))
                {
                    if (start)
                    {
                        StartResult r = t.Start();
                        if (r != StartResult.Ok)
                            throw new Exception("start of task " + t.Name + " failed: " + r);
                    }
                    else
                    {
                        t.Stop(StopMode.Immediate);
                    }
                }

                if (opts.Json)
                    Json.Print(new JObj { { "task", t.Name }, { "action", start ? "start" : "stop" },
                                          { "executionStatus", t.ExecutionStatus.ToString() } });
                else
                    Console.WriteLine(t.Name + " " + (start ? "started" : "stopped") +
                                      " (" + t.ExecutionStatus + ")");
                return 0;
            }
        }
    }
}
