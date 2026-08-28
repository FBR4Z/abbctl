using System;
using System.IO;
using System.Linq;

namespace AbbCtl.Commands
{
    internal static class FsCmd
    {
        public static int Run(GlobalOptions opts, string[] args)
        {
            if (args.Length == 0)
                throw new UsageException("fs requires a subcommand: ls | get | put");

            string[] rest = args.Skip(1).ToArray();
            switch (args[0])
            {
                case "ls": return Ls(opts, rest);
                case "get": return Get(opts, rest);
                case "put": return Put(opts, rest);
                default:
                    throw new UsageException("unknown fs subcommand '" + args[0] + "'");
            }
        }

        private static int Ls(GlobalOptions opts, string[] args)
        {
            using (var s = Session.Open(opts))
            {
                var fs = s.Controller.FileSystem;
                // Listing takes a glob pattern relative to the controller HOME directory.
                string pattern = args.Length > 0 ? args[0] : "*";
                if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                    pattern = pattern.TrimEnd('/') + "/*";

                var entries = fs.GetFilesAndDirectories(pattern);
                var dirs = entries.OfType<ABB.Robotics.Controllers.FileSystemDomain.ControllerDirectoryInfo>()
                                  .Select(e => e.Name).ToList();
                var files = entries.OfType<ABB.Robotics.Controllers.FileSystemDomain.ControllerFileInfo>()
                                   .Select(e => e.Name).ToList();

                if (opts.Json)
                {
                    Json.Print(new JObj
                    {
                        { "pattern", pattern },
                        { "directories", dirs },
                        { "files", files }
                    });
                    return 0;
                }

                Console.WriteLine(fs.RemoteDirectory + " (" + pattern + ")");
                foreach (var d in dirs) Console.WriteLine("  " + d + "/");
                foreach (var f in files) Console.WriteLine("  " + f);
                return 0;
            }
        }

        private static int Get(GlobalOptions opts, string[] args)
        {
            if (args.Length < 1) throw new UsageException("fs get <remote> [local]");
            string remote = args[0];
            string local = args.Length > 1 ? args[1] : Path.GetFileName(remote.Replace('\\', '/'));

            using (var s = Session.Open(opts))
            {
                s.Controller.FileSystem.GetFile(remote, Path.GetFullPath(local), true);
                if (opts.Json) Json.Print(new JObj { { "remote", remote }, { "local", Path.GetFullPath(local) } });
                else Console.WriteLine("downloaded " + remote + " -> " + local);
                return 0;
            }
        }

        private static int Put(GlobalOptions opts, string[] args)
        {
            if (args.Length < 1) throw new UsageException("fs put <local> [remote]");
            string local = Path.GetFullPath(args[0]);
            if (!File.Exists(local)) throw new Exception("file not found: " + local);

            using (var s = Session.Open(opts))
            {
                var fs = s.Controller.FileSystem;
                string remote = args.Length > 1 ? args[1] : Path.GetFileName(local);
                fs.PutFile(local, remote, true);
                if (opts.Json) Json.Print(new JObj { { "local", local }, { "remote", remote } });
                else Console.WriteLine("uploaded " + local + " -> " + remote);
                return 0;
            }
        }
    }
}
