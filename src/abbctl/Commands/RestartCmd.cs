using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace AbbCtl.Commands
{
    internal static class RestartCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            bool noWait = args.Contains("--no-wait");

            string systemName;
            using (var s = Session.Open(opts))
            {
                systemName = s.Controller.SystemName;
                using (ABB.Robotics.Controllers.Mastership.Request(s.Controller))
                    s.Controller.Restart();
            }

            if (noWait)
            {
                if (opts.Json) Json.Print(new JObj { { "restarting", systemName } });
                else Console.WriteLine("warm start issued to " + systemName);
                return 0;
            }

            if (!opts.Json) Console.WriteLine("warm start issued to " + systemName + ", waiting for it to come back...");

            var sw = Stopwatch.StartNew();
            Thread.Sleep(5000);
            while (sw.Elapsed.TotalSeconds < 180)
            {
                try
                {
                    var found = Session.Discover().FirstOrDefault(ci =>
                        string.Equals(ci.SystemName, systemName, StringComparison.OrdinalIgnoreCase) &&
                        ci.Availability.ToString() == "Available");
                    if (found != null)
                    {
                        int secs = (int)sw.Elapsed.TotalSeconds;
                        if (opts.Json) Json.Print(new JObj { { "restarted", systemName }, { "seconds", secs } });
                        else Console.WriteLine(systemName + " is back (" + secs + " s)");
                        return 0;
                    }
                }
                catch { }
                Thread.Sleep(2000);
            }
            throw new Exception(systemName + " did not come back within 180 s; check the controller");
        }
    }
}
