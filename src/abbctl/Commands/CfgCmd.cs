using System;
using System.IO;
using System.Linq;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.ConfigurationDomain;
using CfgType = ABB.Robotics.Controllers.ConfigurationDomain.Type;

namespace AbbCtl.Commands
{
    /// <summary>
    /// Controller configuration database (topics SYS, EIO, MOC, ...). Changes
    /// only take effect after a warm start ('abbctl restart').
    /// </summary>
    internal static class CfgCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            if (args.Length == 0)
                throw new UsageException("cfg requires a subcommand: list | get | set | create | delete | load");

            string[] rest = args.Skip(1).ToArray();
            switch (args[0])
            {
                case "list": return List(opts, rest);
                case "get": return Get(opts, rest);
                case "set": return Set(opts, rest);
                case "create": return Create(opts, rest);
                case "delete": return Delete(opts, rest);
                case "load": return Load(opts, rest);
                default:
                    throw new UsageException("unknown cfg subcommand '" + args[0] + "'");
            }
        }

        private static Domain GetDomain(Controller c, string name)
        {
            Domain d = c.Configuration.Domains.Cast<Domain>().FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (d == null)
                throw new Exception("domain '" + name + "' not found; available: " +
                    string.Join(", ", c.Configuration.Domains.Cast<Domain>().Select(x => x.Name)));
            return d;
        }

        private static CfgType GetType(Domain d, string name)
        {
            CfgType t = d.Types.Cast<CfgType>().FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (t == null)
                throw new Exception("type '" + name + "' not found in " + d.Name + "; available: " +
                    string.Join(", ", d.Types.Cast<CfgType>().Select(x => x.Name)));
            return t;
        }

        private static int List(GlobalOptions opts, string[] args)
        {
            using (var s = Session.Open(opts))
            {
                var c = s.Controller;
                if (args.Length == 0)
                {
                    var names = c.Configuration.Domains.Cast<Domain>().Select(d => d.Name).ToList();
                    if (opts.Json) Json.Print(names);
                    else names.ForEach(Console.WriteLine);
                    return 0;
                }

                Domain dom = GetDomain(c, args[0]);
                if (args.Length == 1)
                {
                    var names = dom.Types.Cast<CfgType>().Select(t => t.Name).ToList();
                    if (opts.Json) Json.Print(names);
                    else names.ForEach(Console.WriteLine);
                    return 0;
                }

                CfgType type = GetType(dom, args[1]);
                var instances = type.GetInstances().Select(i => i.Name).ToList();
                if (opts.Json) Json.Print(instances);
                else instances.ForEach(Console.WriteLine);
                return 0;
            }
        }

        private static int Get(GlobalOptions opts, string[] args)
        {
            if (args.Length < 3)
                throw new UsageException("cfg get <domain> <type> <instance> [attribute]");

            using (var s = Session.Open(opts))
            {
                CfgType type = GetType(GetDomain(s.Controller, args[0]), args[1]);
                Instance inst = type.GetInstance(args[2]);
                if (inst == null) throw new Exception("instance '" + args[2] + "' not found");

                if (args.Length >= 4)
                {
                    object v = inst.GetAttribute(args[3]);
                    if (opts.Json) Json.Print(new JObj { { "attribute", args[3] }, { "value", v == null ? null : v.ToString() } });
                    else Console.WriteLine(v);
                    return 0;
                }

                var all = new JObj();
                foreach (ABB.Robotics.Controllers.ConfigurationDomain.Attribute a in type.Attributes)
                {
                    object v;
                    try { v = inst.GetAttribute(a.Name); } catch { v = "(unreadable)"; }
                    all[a.Name] = v == null ? null : v.ToString();
                    if (!opts.Json) Console.WriteLine("{0,-24} {1}", a.Name, v);
                }
                if (opts.Json) Json.Print(all);
                return 0;
            }
        }

        private static int Set(GlobalOptions opts, string[] args)
        {
            if (args.Length < 5)
                throw new UsageException("cfg set <domain> <type> <instance> <attribute> <value>");

            using (var s = Session.Open(opts))
            {
                CfgType type = GetType(GetDomain(s.Controller, args[0]), args[1]);
                Instance inst = type.GetInstance(args[2]);
                if (inst == null) throw new Exception("instance '" + args[2] + "' not found");

                using (Mastership.Request(s.Controller))
                    inst.SetAttribute(args[3], string.Join(" ", args.Skip(4)));

                Report(opts, "set", args[0] + "/" + args[1] + "/" + args[2] + "/" + args[3]);
                return 0;
            }
        }

        private static int Create(GlobalOptions opts, string[] args)
        {
            bool force = args.Contains("--force");
            args = args.Where(a => a != "--force").ToArray();
            if (args.Length < 3)
                throw new UsageException("cfg create <domain> <type> <name> [Attribute=Value ...] [--force]");

            // Lesson learned the hard way: a SEMISTATIC/STATIC task whose Entry
            // routine does not exist yet refuses to start at boot and puts the
            // whole controller in SYSFAIL, unrecoverable remotely.
            bool riskyTask = args[0].Equals("SYS", StringComparison.OrdinalIgnoreCase) &&
                args[1].Equals("CAB_TASKS", StringComparison.OrdinalIgnoreCase) &&
                args.Skip(3).Any(p =>
                    p.StartsWith("Type=", StringComparison.OrdinalIgnoreCase) &&
                    (p.EndsWith("SEMISTATIC", StringComparison.OrdinalIgnoreCase) ||
                     p.EndsWith("STATIC", StringComparison.OrdinalIgnoreCase)));
            if (riskyTask && !force)
                throw new UsageException(
                    "creating a SEMISTATIC/STATIC task directly causes SYSFAIL at the next restart " +
                    "if its Entry routine does not exist yet. Safe sequence: create with Type=NORMAL, " +
                    "restart, load a module containing the entry routine into the new task, then " +
                    "'cfg set SYS CAB_TASKS <name> Type SEMISTATIC' and restart again. " +
                    "Use --force only if the task program already exists.");

            using (var s = Session.Open(opts))
            {
                CfgType type = GetType(GetDomain(s.Controller, args[0]), args[1]);
                using (Mastership.Request(s.Controller))
                {
                    Instance inst = type.Create(args[2]);
                    foreach (string pair in args.Skip(3))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq <= 0)
                            throw new UsageException("attribute must be Name=Value, got '" + pair + "'");
                        inst.SetAttribute(pair.Substring(0, eq), pair.Substring(eq + 1));
                    }
                }
                Report(opts, "created", args[0] + "/" + args[1] + "/" + args[2]);
                return 0;
            }
        }

        private static int Delete(GlobalOptions opts, string[] args)
        {
            if (args.Length < 3)
                throw new UsageException("cfg delete <domain> <type> <instance>");

            using (var s = Session.Open(opts))
            {
                CfgType type = GetType(GetDomain(s.Controller, args[0]), args[1]);
                Instance inst = type.GetInstance(args[2]);
                if (inst == null) throw new Exception("instance '" + args[2] + "' not found");

                using (Mastership.Request(s.Controller))
                    inst.Delete();

                Report(opts, "deleted", args[0] + "/" + args[1] + "/" + args[2]);
                return 0;
            }
        }

        private static int Load(GlobalOptions opts, string[] args)
        {
            if (args.Length < 1)
                throw new UsageException("cfg load <file.cfg> [--mode add|replace|resetadd]");
            string file = args[0];
            if (!File.Exists(file)) throw new Exception("file not found: " + file);

            LoadMode mode = LoadMode.Add;
            for (int i = 1; i < args.Length; i++)
                if (args[i] == "--mode" && i + 1 < args.Length)
                {
                    switch (args[i + 1].ToLowerInvariant())
                    {
                        case "add": mode = LoadMode.Add; break;
                        case "replace": mode = LoadMode.Replace; break;
                        case "resetadd": mode = LoadMode.ResetAndAdd; break;
                        default: throw new UsageException("mode must be add, replace or resetadd");
                    }
                }

            using (var s = Session.Open(opts))
            {
                var fs = s.Controller.FileSystem;
                string remoteName = "abbctl_" + Path.GetFileName(file);
                fs.PutFile(Path.GetFullPath(file), remoteName, true);
                try
                {
                    using (Mastership.Request(s.Controller))
                        s.Controller.Configuration.Load(fs.RemoteDirectory + "/" + remoteName, mode);
                }
                finally
                {
                    try { fs.RemoveFile(remoteName); } catch { }
                }
                Report(opts, "loaded", file + " (" + mode + ")");
                return 0;
            }
        }

        private static void Report(GlobalOptions opts, string action, string what)
        {
            if (opts.Json)
                Json.Print(new JObj { { action, what }, { "note", "restart required to take effect ('abbctl restart')" } });
            else
                Console.WriteLine(action + " " + what + "  [restart required to take effect: 'abbctl restart']");
        }
    }
}
