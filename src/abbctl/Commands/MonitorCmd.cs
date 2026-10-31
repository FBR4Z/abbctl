using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.EventLogDomain;
using ABB.Robotics.Controllers.MotionDomain;
using ABB.Robotics.Controllers.RapidDomain;

namespace AbbCtl.Commands
{
    /// <summary>
    /// Read-only live feed for HMIs (Hefesto): one connection, one NDJSON line per
    /// change. Starts with a "snapshot", then "state", "opmode", "exec" and "log"
    /// as the controller reports them, and "speed" / "joints" sampled every
    /// --interval seconds (only when they change), "pp" (program pointer of the
    /// motion task) while it moves and "io" for the --io signals. Ends with "disconnected"
    /// (exit 4) when the controller goes away, so the caller can restart it.
    /// </summary>
    internal static class MonitorCmd
    {
        private static readonly object Gate = new object();

        public static int Run(GlobalOptions opts, string[] args)
        {
            double interval = 1.0;
            var signals = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--interval" && i + 1 < args.Length)
                    interval = Math.Max(0.2, double.Parse(args[++i], CultureInfo.InvariantCulture));
                else if (args[i] == "--io" && i + 1 < args.Length)
                    signals.AddRange(args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                else if (args[i] == "--parent-stdin")
                    WatchParent();
                else
                    throw new UsageException("monitor [--interval <s>] [--io SIG1,SIG2] [--parent-stdin]");
            }

            using (var s = Session.Open(opts))
            {
                var c = s.Controller;
                MechanicalUnit robot = c.MotionSystem.MechanicalUnits
                    .Cast<MechanicalUnit>()
                    .FirstOrDefault(u => u.Type == MechanicalUnitType.TcpRobot);

                double[] joints = ReadJoints(robot);
                int speed = c.MotionSystem.SpeedRatio;
                ABB.Robotics.Controllers.RapidDomain.Task motionTask = c.Rapid.GetTasks()
                    .FirstOrDefault(t => t.Motion) ?? c.Rapid.GetTasks().FirstOrDefault();
                string pp = Pointer(motionTask);
                Emit(new JObj
                {
                    { "type", "snapshot" },
                    { "systemName", c.SystemName },
                    { "ip", s.Info.IPAddress.ToString() },
                    { "virtual", c.IsVirtual },
                    { "robotwareVersion", c.RobotWareVersion.ToString() },
                    { "operatingMode", c.OperatingMode.ToString() },
                    { "state", c.State.ToString() },
                    { "executionStatus", c.Rapid.ExecutionStatus.ToString() },
                    { "speedRatio", speed },
                    { "robot", robot != null ? robot.Name : null },
                    { "joints", joints },
                    { "task", motionTask != null ? motionTask.Name : null },
                    { "pp", pp }
                });

                foreach (string name in signals)
                {
                    try
                    {
                        var signal = (ABB.Robotics.Controllers.IOSystemDomain.Signal)c.IOSystem.GetSignal(name);
                        Emit(new JObj { { "type", "io" }, { "time", Now() }, { "name", name }, { "value", signal.Value } });
                        signal.Changed += (sender, e) => Emit(new JObj
                        {
                            { "type", "io" }, { "time", Now() }, { "name", name }, { "value", e.NewSignalState.Value }
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("monitor: signal " + name + ": " + ex.Message);
                    }
                }

                c.StateChanged += (sender, e) => Emit(Change("state", e.NewState.ToString()));
                c.OperatingModeChanged += (sender, e) => Emit(Change("opmode", e.NewMode.ToString()));
                c.Rapid.ExecutionStatusChanged += (sender, e) => Emit(Change("exec", e.Status.ToString()));
                c.EventLog.MessageWritten += (sender, e) =>
                {
                    EventLogMessage m = e.Message;
                    Emit(new JObj
                    {
                        { "type", "log" },
                        { "time", Now() },
                        { "severity", Severity(m.Type) },
                        { "code", m.CategoryId + "-" + m.Number },
                        { "title", m.Title },
                        { "body", m.Body }
                    });
                };

                while (true)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(interval));
                    try
                    {
                        if (!c.Connected)
                            break;
                        int newSpeed = c.MotionSystem.SpeedRatio;
                        if (newSpeed != speed)
                        {
                            speed = newSpeed;
                            Emit(Change("speed", speed));
                        }
                        string newPp = Pointer(motionTask);
                        if (newPp != pp)
                        {
                            pp = newPp;
                            Emit(Change("pp", pp));
                        }
                        double[] newJoints = ReadJoints(robot);
                        if (Moved(joints, newJoints))
                        {
                            joints = newJoints;
                            Emit(Change("joints", joints));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("monitor: " + ex.Message);
                        break;
                    }
                }
                Emit(Change("disconnected", c.SystemName));
                return 4;
            }
        }

        /// <summary>
        /// --parent-stdin: the caller keeps our stdin open; when it ends (the caller
        /// exited or crashed), exit too instead of monitoring forever as an orphan.
        /// </summary>
        private static void WatchParent()
        {
            var thread = new Thread(() =>
            {
                try { while (Console.In.Read() >= 0) { } } catch { }
                Environment.Exit(5);
            }) { IsBackground = true };
            thread.Start();
        }

        /// <summary>"Module/routine:row" of the program pointer, or null.</summary>
        private static string Pointer(ABB.Robotics.Controllers.RapidDomain.Task task)
        {
            try
            {
                var p = task?.ProgramPointer;
                return p == null || string.IsNullOrEmpty(p.Module) ? null : p.Module + "/" + p.Routine + ":" + p.Range.Begin.Row;
            }
            catch { return null; }
        }

        private static double[] ReadJoints(MechanicalUnit robot)
        {
            if (robot == null)
                return null;
            var ax = robot.GetPosition().RobAx;
            return new[] { ax.Rax_1, ax.Rax_2, ax.Rax_3, ax.Rax_4, ax.Rax_5, ax.Rax_6 }
                .Select(v => Math.Round((double)v, 2))
                .ToArray();
        }

        private static bool Moved(double[] before, double[] after)
        {
            if (before == null || after == null)
                return before != after;
            for (int i = 0; i < before.Length; i++)
                if (Math.Abs(before[i] - after[i]) >= 0.1)
                    return true;
            return false;
        }

        private static string Severity(EventLogEntryType type)
        {
            switch (type)
            {
                case EventLogEntryType.Error: return "error";
                case EventLogEntryType.Warning: return "warning";
                default: return "info";
            }
        }

        private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        private static JObj Change(string type, object value) =>
            new JObj { { "type", type }, { "time", Now() }, { "value", value } };

        private static void Emit(JObj o)
        {
            lock (Gate)
            {
                Json.Print(o);
                Console.Out.Flush();
            }
        }
    }
}
