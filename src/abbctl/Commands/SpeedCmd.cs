using System;
using ABB.Robotics.Controllers;

namespace AbbCtl.Commands
{
    internal static class SpeedCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            using (var s = Session.Open(opts))
            {
                var motion = s.Controller.MotionSystem;

                if (args.Length > 0)
                {
                    int value;
                    string arg = args[0].TrimEnd('%');
                    if (!int.TryParse(arg, out value) || value < 0 || value > 100)
                        throw new UsageException("speed [0-100] — e.g. 'abbctl speed 25'");

                    using (Mastership.Request(s.Controller))
                        motion.SpeedRatio = value;
                }

                int current = motion.SpeedRatio;
                if (opts.Json) Json.Print(new JObj { { "speedRatio", current } });
                else Console.WriteLine(current + "%");
                return 0;
            }
        }
    }
}
