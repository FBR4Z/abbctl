using System;
using System.Collections.Generic;
using System.Linq;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.Discovery;

namespace AbbCtl
{
    /// <summary>
    /// Resolves the target controller (by ip / name / guid, env var, or the only
    /// one on the network), connects and logs on. Dispose releases the connection.
    /// </summary>
    internal sealed class Session : IDisposable
    {
        public Controller Controller { get; private set; }
        public ControllerInfo Info { get; private set; }

        public static List<ControllerInfo> Discover()
        {
            var scanner = new NetworkScanner();
            scanner.Scan();
            return scanner.Controllers.Cast<ControllerInfo>().ToList();
        }

        public static Session Open(GlobalOptions opts)
        {
            var found = Discover();
            ControllerInfo target = null;

            if (!string.IsNullOrEmpty(opts.Controller))
            {
                string want = opts.Controller.Trim();
                target = found.FirstOrDefault(ci =>
                    string.Equals(ci.IPAddress.ToString(), want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ci.SystemName, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ci.Id.ToString(), want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ci.ControllerName, want, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    throw new Exception("controller '" + want + "' not found on the network. Run 'abbctl scan'.");
            }
            else if (found.Count == 1)
            {
                target = found[0];
            }
            else if (found.Count == 0)
            {
                throw new Exception("no controllers found on the network. For a virtual controller, start the station in RobotStudio first.");
            }
            else
            {
                string names = string.Join(", ", found.Select(ci => ci.SystemName + " (" + ci.IPAddress + ")"));
                throw new Exception("multiple controllers found, specify one with -c <ip|name>: " + names);
            }

            var session = new Session();
            session.Info = target;
            session.Controller = Controller.Connect(target, ConnectionType.Standalone);
            session.Controller.Logon(BuildUser(opts));
            return session;
        }

        private static UserInfo BuildUser(GlobalOptions opts)
        {
            if (opts.User == "Default User" && opts.Password == "robotics")
                return UserInfo.DefaultUser;
            return new UserInfo(opts.User, opts.Password);
        }

        public void Dispose()
        {
            if (Controller != null)
            {
                try { Controller.Logoff(); } catch { }
                try { Controller.Dispose(); } catch { }
                Controller = null;
            }
        }
    }
}
