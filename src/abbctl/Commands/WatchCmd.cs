using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.EventLogDomain;
using ABB.Robotics.Controllers.IOSystemDomain;
using ABB.Robotics.Controllers.RapidDomain;

namespace AbbCtl.Commands
{
    /// <summary>
    /// Blocks on controller events instead of polling. Default: exit 0 on the
    /// first change (or when --until matches). --follow streams until Ctrl+C.
    /// --timeout exits 3 if the awaited change never happens (0 with --follow).
    /// </summary>
    internal static class WatchCmd
    {
        private static readonly object Gate = new object();
        private static readonly System.Collections.Generic.Dictionary<string, string> LastEmitted =
            new System.Collections.Generic.Dictionary<string, string>();

        private sealed class WatchOptions
        {
            public string Until;
            public double? TimeoutSec;
            public bool Follow;
            public string[] Positional;
        }

        public static int Run(GlobalOptions opts, string[] args)
        {
            if (args.Length == 0)
                throw new UsageException("watch requires a subcommand: io | rapid | exec | state | log");

            var w = Parse(args.Skip(1).ToArray());
            switch (args[0])
            {
                case "io": return WatchIo(opts, w);
                case "rapid": return WatchRapid(opts, w);
                case "exec": return WatchExec(opts, w);
                case "state": return WatchState(opts, w);
                case "log": return WatchLog(opts, w);
                default:
                    throw new UsageException("unknown watch subcommand '" + args[0] + "'");
            }
        }

        private static WatchOptions Parse(string[] args)
        {
            var w = new WatchOptions();
            var positional = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--until":
                        if (++i >= args.Length) throw new UsageException("missing value for --until");
                        w.Until = args[i];
                        break;
                    case "--timeout":
                        if (++i >= args.Length) throw new UsageException("missing value for --timeout");
                        w.TimeoutSec = double.Parse(args[i], CultureInfo.InvariantCulture);
                        break;
                    case "--follow":
                        w.Follow = true;
                        break;
                    default:
                        positional.Add(args[i]);
                        break;
                }
            }
            w.Positional = positional.ToArray();
            return w;
        }

        private static void Emit(GlobalOptions opts, string type, string name, object value)
        {
            lock (Gate)
            {
                // The SDK fires an initial event on subscription; suppress
                // consecutive duplicates so each line is a real change.
                string key = type + "/" + name;
                string val = Convert.ToString(value, CultureInfo.InvariantCulture);
                string last;
                if (LastEmitted.TryGetValue(key, out last) && last == val)
                    return;
                LastEmitted[key] = val;

                if (opts.Json)
                    Json.Print(new JObj
                    {
                        { "time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") },
                        { "type", type },
                        { "name", name },
                        { "value", value }
                    });
                else
                    Console.WriteLine("{0:HH:mm:ss.fff} {1,-6} {2} = {3}", DateTime.Now, type, name, value);
                Console.Out.Flush();
            }
        }

        /// <summary>Signals completion when a change (matching --until, if given) arrives.</summary>
        private static void Arm(WatchOptions w, ManualResetEventSlim done, string observed)
        {
            if (w.Follow) return;
            if (w.Until == null || ValuesEqual(w.Until, observed))
                done.Set();
        }

        private static bool ValuesEqual(string expected, string observed)
        {
            if (string.Equals(expected.Trim(), observed.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            double a, b;
            if (double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                double.TryParse(observed, NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                return Math.Abs(a - b) < 1e-9;
            return false;
        }

        private static int WaitForDone(ManualResetEventSlim done, WatchOptions w)
        {
            if (w.TimeoutSec.HasValue)
            {
                if (done.Wait(TimeSpan.FromSeconds(w.TimeoutSec.Value)))
                    return 0;
                if (w.Follow)
                    return 0; // timed observation window, not a failed wait
                Console.Error.WriteLine("timeout: awaited change did not happen within " + w.TimeoutSec.Value + " s");
                return 3;
            }
            done.Wait();
            return 0;
        }

        private static int WatchIo(GlobalOptions opts, WatchOptions w)
        {
            if (w.Positional.Length < 1)
                throw new UsageException("watch io <signal> [--until <value>] [--follow] [--timeout <s>]");

            using (var s = Session.Open(opts))
            {
                Signal sig = s.Controller.IOSystem.GetSignal(w.Positional[0]);
                if (sig == null) throw new Exception("signal '" + w.Positional[0] + "' not found");

                var done = new ManualResetEventSlim(false);
                Emit(opts, "io", sig.Name, sig.Value);
                // The awaited value may already be present before any event fires.
                Arm(w, done, sig.Value.ToString(CultureInfo.InvariantCulture));

                sig.Changed += (sender, e) =>
                {
                    float v = e.NewSignalState.Value;
                    Emit(opts, "io", sig.Name, v);
                    Arm(w, done, v.ToString(CultureInfo.InvariantCulture));
                };
                return WaitForDone(done, w);
            }
        }

        private static int WatchRapid(GlobalOptions opts, WatchOptions w)
        {
            if (w.Positional.Length < 3)
                throw new UsageException("watch rapid <task> <module> <symbol> [--until <value>] [--follow] [--timeout <s>] (PERS only)");

            using (var s = Session.Open(opts))
            using (RapidData rd = s.Controller.Rapid.GetRapidData(w.Positional[0], w.Positional[1], w.Positional[2]))
            {
                string name = string.Join("/", w.Positional.Take(3));
                var done = new ManualResetEventSlim(false);
                string initial = rd.Value.ToString();
                Emit(opts, "rapid", name, initial);
                Arm(w, done, initial);

                rd.ValueChanged += (sender, e) =>
                {
                    string v;
                    try { v = rd.Value.ToString(); } catch { return; }
                    Emit(opts, "rapid", name, v);
                    Arm(w, done, v);
                };
                return WaitForDone(done, w);
            }
        }

        private static int WatchExec(GlobalOptions opts, WatchOptions w)
        {
            using (var s = Session.Open(opts))
            {
                var done = new ManualResetEventSlim(false);
                string initial = s.Controller.Rapid.ExecutionStatus.ToString();
                Emit(opts, "exec", "executionStatus", initial);
                Arm(w, done, initial);

                s.Controller.Rapid.ExecutionStatusChanged += (sender, e) =>
                {
                    Emit(opts, "exec", "executionStatus", e.Status.ToString());
                    Arm(w, done, e.Status.ToString());
                };
                return WaitForDone(done, w);
            }
        }

        private static int WatchState(GlobalOptions opts, WatchOptions w)
        {
            using (var s = Session.Open(opts))
            {
                var done = new ManualResetEventSlim(false);
                Emit(opts, "state", "controllerState", s.Controller.State.ToString());
                Emit(opts, "state", "operatingMode", s.Controller.OperatingMode.ToString());
                Arm(w, done, s.Controller.State.ToString());
                Arm(w, done, s.Controller.OperatingMode.ToString());

                s.Controller.StateChanged += (sender, e) =>
                {
                    Emit(opts, "state", "controllerState", e.NewState.ToString());
                    Arm(w, done, e.NewState.ToString());
                };
                s.Controller.OperatingModeChanged += (sender, e) =>
                {
                    Emit(opts, "state", "operatingMode", e.NewMode.ToString());
                    Arm(w, done, e.NewMode.ToString());
                };
                return WaitForDone(done, w);
            }
        }

        private static int WatchLog(GlobalOptions opts, WatchOptions w)
        {
            // Log watching is inherently a stream; no meaningful single "change".
            w.Follow = true;

            using (var s = Session.Open(opts))
            {
                var done = new ManualResetEventSlim(false);
                s.Controller.EventLog.MessageWritten += (sender, e) =>
                {
                    EventLogMessage m = e.Message;
                    Emit(opts, "log", m.CategoryId + "-" + m.Number + " [" + m.Type + "]", m.Title);
                };
                if (!opts.Json)
                    Console.Error.WriteLine("watching event log (Ctrl+C to stop)...");
                return WaitForDone(done, w);
            }
        }
    }
}
