using System;
using System.Linq;
using ABB.Robotics.Controllers.MotionDomain;

namespace AbbCtl.Commands
{
    internal static class InfoCmd
    {
        public static int RunInfo(GlobalOptions opts, string[] args)
        {
            using (var s = Session.Open(opts))
            {
                var c = s.Controller;
                var o = new JObj
                {
                    { "systemName", c.SystemName },
                    { "ip", s.Info.IPAddress.ToString() },
                    { "virtual", c.IsVirtual },
                    { "robotwareVersion", c.RobotWareVersion.ToString() },
                    { "operatingMode", c.OperatingMode.ToString() },
                    { "state", c.State.ToString() },
                    { "executionStatus", c.Rapid.ExecutionStatus.ToString() },
                    { "speedRatio", c.MotionSystem.SpeedRatio },
                    { "backupInProgress", c.BackupInProgress }
                };

                if (opts.Json) { Json.Print(o); return 0; }

                foreach (var kv in o)
                    Console.WriteLine("{0,-18} {1}", kv.Key + ":", kv.Value);
                return 0;
            }
        }

        /// <summary>
        /// The TCP as a RAPID robtarget in the ACTIVE work object, with the active
        /// tool and wobj names: what a "teach this point" needs.
        /// </summary>
        private static int RunPosRapid(GlobalOptions opts)
        {
            using (var s = Session.Open(opts))
            {
                var c = s.Controller;
                MechanicalUnit unit = c.MotionSystem.ActiveMechanicalUnit;
                var rt = unit.GetPosition(CoordinateSystemType.WorkObject);
                var o = new JObj
                {
                    { "unit", unit.Name },
                    { "tool", unit.Tool },
                    { "wobj", unit.WorkObject },
                    { "robtarget", rt.ToString() }
                };
                if (opts.Json) { Json.Print(o); return 0; }
                Console.WriteLine("{0} (tool {1}, wobj {2}): {3}", unit.Name, unit.Tool, unit.WorkObject, rt);
                return 0;
            }
        }

        public static int RunPos(GlobalOptions opts, string[] args)
        {
            bool joints = args.Contains("--joints");
            if (args.Contains("--rapid"))
                return RunPosRapid(opts);

            using (var s = Session.Open(opts))
            {
                var c = s.Controller;
                var results = new System.Collections.Generic.List<object>();

                foreach (MechanicalUnit unit in c.MotionSystem.MechanicalUnits)
                {
                    var o = new JObj { { "unit", unit.Name } };
                    if (joints)
                    {
                        var jt = unit.GetPosition();
                        o["joints"] = jt.ToString();
                    }
                    else
                    {
                        var rt = unit.GetPosition(CoordinateSystemType.World);
                        o["robtarget"] = rt.ToString();
                    }
                    results.Add(o);

                    if (!opts.Json)
                        Console.WriteLine("{0}: {1}", unit.Name, joints ? (string)((JObj)o)["joints"] : (string)((JObj)o)["robtarget"]);
                }

                if (opts.Json) Json.Print(results);
                return 0;
            }
        }
    }
}
