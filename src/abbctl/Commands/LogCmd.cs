using System;
using System.Collections.Generic;
using System.Linq;
using ABB.Robotics.Controllers.EventLogDomain;

namespace AbbCtl.Commands
{
    internal static class LogCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            int count = 20;
            for (int i = 0; i < args.Length; i++)
                if ((args[i] == "-n" || args[i] == "--count") && i + 1 < args.Length)
                    count = int.Parse(args[++i]);

            using (var s = Session.Open(opts))
            {
                var all = new List<EventLogMessage>();
                foreach (EventLogCategory cat in s.Controller.EventLog.GetCategories())
                {
                    foreach (EventLogMessage msg in cat.Messages)
                        all.Add(msg);
                }

                var recent = all.OrderByDescending(m => m.Timestamp).Take(count).ToList();
                var rows = new List<object>();
                foreach (var m in recent)
                {
                    rows.Add(new JObj
                    {
                        { "timestamp", m.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") },
                        { "type", m.Type.ToString() },
                        { "code", m.CategoryId + "-" + m.Number },
                        { "title", m.Title }
                    });
                    if (!opts.Json)
                        Console.WriteLine("{0:yyyy-MM-dd HH:mm:ss} [{1,-7}] {2,-8} {3}",
                            m.Timestamp, m.Type, m.CategoryId + "-" + m.Number, m.Title);
                }
                if (opts.Json) Json.Print(rows);
                return 0;
            }
        }
    }
}
