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
    /// --interval seconds (only when they change). Ends with "disconnected"
    /// (exit 4) when the controller goes away, so the caller can restart it.
    /// </summary>
    internal static class MonitorCmd
    {
        private static readonly object Gate = new object();

        public static int Run(GlobalOptions opts, string[] args)
        {
            double interval = 1.0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--interval" && i + 1 < args.Length)
                    interval = Math.Max(0.2, double.Parse(args[++i], CultureInfo.InvariantCulture));
                else
                    throw new UsageException("monitor [--interval <s>]");
            }

            using (var s = Session.Open(opts))
            {
                var c = s.Controller;
                MechanicalUnit robot = c.MotionSystem.MechanicalUnits
                    .Cast<MechanicalUnit>()
                    .FirstOrDefault(u => u.Type == MechanicalUnitType.TcpRobot);

                double[] joints = ReadJoints(robot);
                int speed = c.MotionSystem.SpeedRatio;
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
                    { "joints", joints }
                });

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
                        { "title", m.Title }
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
