using System;
using System.Globalization;
using System.Linq;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.IOSystemDomain;

namespace AbbCtl.Commands
{
    internal static class IoCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            if (args.Length == 0)
                throw new UsageException("io requires a subcommand: list | get | set");

            switch (args[0])
            {
                case "list": return List(opts, args.Skip(1).ToArray());
                case "get": return Get(opts, args.Skip(1).ToArray());
                case "set": return Set(opts, args.Skip(1).ToArray());
                case "create": return Create(opts, args.Skip(1).ToArray());
                default:
                    throw new UsageException("unknown io subcommand '" + args[0] + "'");
            }
        }

        private static int List(GlobalOptions opts, string[] args)
        {
            string filter = null;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];

            using (var s = Session.Open(opts))
            {
                SignalCollection signals = s.Controller.IOSystem.GetSignals(IOFilterTypes.All);
                var rows = new System.Collections.Generic.List<object>();

                foreach (Signal sig in signals)
                {
                    if (filter != null && sig.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    rows.Add(new JObj
                    {
                        { "name", sig.Name },
                        { "type", sig.Type.ToString() },
                        { "value", sig.Value }
                    });
                    if (!opts.Json)
                        Console.WriteLine("{0,-32} {1,-10} {2}", sig.Name, sig.Type, sig.Value);
                }

                if (opts.Json) Json.Print(rows);
                return 0;
            }
        }

        private static int Get(GlobalOptions opts, string[] args)
        {
            if (args.Length < 1) throw new UsageException("io get <signal>");

            using (var s = Session.Open(opts))
            {
                Signal sig = s.Controller.IOSystem.GetSignal(args[0]);
                if (sig == null) throw new Exception("signal '" + args[0] + "' not found");

                if (opts.Json)
                    Json.Print(new JObj { { "name", sig.Name }, { "type", sig.Type.ToString() }, { "value", sig.Value } });
                else
                    Console.WriteLine(sig.Value.ToString(CultureInfo.InvariantCulture));
                return 0;
            }
        }

        /// <summary>
        /// Creates an EIO_SIGNAL configuration instance. Without --device the
        /// signal has no I/O device (a "virtual"/memory signal). Takes effect
        /// only after a warm start ('abbctl restart').
        /// </summary>
        private static int Create(GlobalOptions opts, string[] args)
        {
            if (args.Length < 1)
                throw new UsageException("io create <name> --type DI|DO|AI|AO|GI|GO [--access ALL] [--device <dev> --map <n>] [--category <c>]");

            string name = args[0];
            string sigType = null, access = "All", device = null, map = null, category = null;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--type": if (++i < args.Length) sigType = args[i]; break;
                    case "--access": if (++i < args.Length) access = args[i]; break;
                    case "--device": if (++i < args.Length) device = args[i]; break;
                    case "--map": if (++i < args.Length) map = args[i]; break;
                    case "--category": if (++i < args.Length) category = args[i]; break;
                }
            }
            if (sigType == null)
                throw new UsageException("io create requires --type DI|DO|AI|AO|GI|GO");

            using (var s = Session.Open(opts))
            {
                var eio = s.Controller.Configuration.Domains.Cast<ABB.Robotics.Controllers.ConfigurationDomain.Domain>()
                    .First(d => d.Name == "EIO");
                var type = eio.Types.Cast<ABB.Robotics.Controllers.ConfigurationDomain.Type>()
                    .First(t => t.Name == "EIO_SIGNAL");

                using (Mastership.Request(s.Controller))
                {
                    var inst = type.Create(name);
                    inst.SetAttribute("SignalType", sigType.ToUpperInvariant());
                    inst.SetAttribute("Access", access);
                    if (device != null) inst.SetAttribute("Device", device);
                    if (map != null) inst.SetAttribute("DeviceMap", map);
                    if (category != null) inst.SetAttribute("Category", category);
                }

                if (opts.Json)
                    Json.Print(new JObj { { "created", name }, { "type", sigType.ToUpperInvariant() },
                        { "access", access }, { "note", "restart required ('abbctl restart')" } });
                else
                    Console.WriteLine("created signal " + name + " (" + sigType.ToUpperInvariant() +
                        ", Access=" + access + ")  [restart required: 'abbctl restart']");
                return 0;
            }
        }

        private static int Set(GlobalOptions opts, string[] args)
        {
            if (args.Length < 2) throw new UsageException("io set <signal> <value>");
            float value = float.Parse(args[1], CultureInfo.InvariantCulture);

            using (var s = Session.Open(opts))
            {
                Signal sig = s.Controller.IOSystem.GetSignal(args[0]);
                if (sig == null) throw new Exception("signal '" + args[0] + "' not found");

                try
                {
                    sig.Value = value;
                }
                catch (Exception ex) when (ex.Message.Contains("safety access restriction"))
                {
                    throw new Exception("controller rejected the write for signal '" + sig.Name +
                        "': its Access Level does not allow remote clients. Set 'Access Level' to ALL " +
                        "for this signal in the I/O configuration (EIO) and restart the controller.");
                }

                float readback = sig.Value;
                if (opts.Json)
                    Json.Print(new JObj { { "name", sig.Name }, { "value", readback } });
                else
                    Console.WriteLine("{0} = {1}", sig.Name, readback.ToString(CultureInfo.InvariantCulture));
                return 0;
            }
        }
    }
}
