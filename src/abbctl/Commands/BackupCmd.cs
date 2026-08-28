using System;

namespace AbbCtl.Commands
{
    internal static class BackupCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            string name = args.Length > 0
                ? args[0]
                : "abbctl_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            using (var s = Session.Open(opts))
            {
                var c = s.Controller;
                string remotePath = c.FileSystem.RemoteDirectory + "/" + name;

                c.Backup(remotePath);

                // Backup runs asynchronously on the controller.
                int waited = 0;
                while (c.BackupInProgress && waited < 300)
                {
                    System.Threading.Thread.Sleep(1000);
                    waited++;
                }
                if (c.BackupInProgress)
                    throw new Exception("backup still in progress after 300 s; check the controller");

                if (opts.Json) Json.Print(new JObj { { "backup", remotePath }, { "seconds", waited } });
                else Console.WriteLine("backup created at " + remotePath + " (controller disk, " + waited + " s)");
                return 0;
            }
        }
    }
}
