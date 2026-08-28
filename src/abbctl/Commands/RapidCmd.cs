using System;
using System.Linq;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.RapidDomain;

namespace AbbCtl.Commands
{
    internal static class RapidCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            if (args.Length == 0)
                throw new UsageException("rapid requires a subcommand: get | set");

            switch (args[0])
            {
                case "get": return Get(opts, args.Skip(1).ToArray());
                case "set": return Set(opts, args.Skip(1).ToArray());
                default:
                    throw new UsageException("unknown rapid subcommand '" + args[0] + "'");
            }
        }

        private static int Get(GlobalOptions opts, string[] args)
        {
            if (args.Length < 3) throw new UsageException("rapid get <task> <module> <symbol>");

            using (var s = Session.Open(opts))
            using (RapidData rd = s.Controller.Rapid.GetRapidData(args[0], args[1], args[2]))
            {
                string value = rd.Value.ToString();
                if (opts.Json)
                    Json.Print(new JObj
                    {
                        { "task", args[0] }, { "module", args[1] }, { "symbol", args[2] },
                        { "type", rd.RapidType }, { "value", value }
                    });
                else
                    Console.WriteLine(value);
                return 0;
            }
        }

        private static int Set(GlobalOptions opts, string[] args)
        {
            if (args.Length < 4) throw new UsageException("rapid set <task> <module> <symbol> <value>");
            string newValue = string.Join(" ", args.Skip(3));

            using (var s = Session.Open(opts))
            using (RapidData rd = s.Controller.Rapid.GetRapidData(args[0], args[1], args[2]))
            {
                using (Mastership.Request(s.Controller))
                {
                    IRapidData val = rd.Value;
                    val.FillFromString(newValue);
                    rd.Value = val;
                }

                string readback = rd.Value.ToString();
                if (opts.Json)
                    Json.Print(new JObj
                    {
                        { "task", args[0] }, { "module", args[1] }, { "symbol", args[2] },
                        { "value", readback }
                    });
                else
                    Console.WriteLine(readback);
                return 0;
            }
        }
    }
}
