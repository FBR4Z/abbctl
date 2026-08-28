using System;
using System.Linq;

namespace AbbCtl.Commands
{
    internal static class ScanCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            var found = Session.Discover();

            if (opts.Json)
            {
                var list = found.Select(ci => (object)new JObj
                {
                    { "systemName", ci.SystemName },
                    { "ip", ci.IPAddress.ToString() },
                    { "id", ci.Id.ToString() },
                    { "version", ci.Version.ToString() },
                    { "virtual", ci.IsVirtual },
                    { "availability", ci.Availability.ToString() }
                }).ToList();
                Json.Print(list);
                return 0;
            }

            if (found.Count == 0)
            {
                Console.WriteLine("no controllers found (for a virtual controller, start the station in RobotStudio first)");
                return 0;
            }

            foreach (var ci in found)
            {
                Console.WriteLine("{0,-20} {1,-16} {2,-8} RW {3}  {4}",
                    ci.SystemName,
                    ci.IPAddress,
                    ci.IsVirtual ? "virtual" : "real",
                    ci.Version,
                    ci.Availability);
            }
            return 0;
        }
    }
}
