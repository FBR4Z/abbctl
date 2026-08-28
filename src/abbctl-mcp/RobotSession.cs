using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.Discovery;

namespace AbbCtl.Mcp;

/// <summary>
/// One persistent controller connection shared by all tools. Connects lazily
/// (pinned target, ABBCTL_CONTROLLER env var, or the only controller found)
/// and reconnects transparently if the connection drops.
/// </summary>
internal static class RobotSession
{
    private static readonly object Gate = new();
    private static Controller? _controller;
    private static string? _pinnedTarget;

    public static List<ControllerInfo> Discover()
    {
        var scanner = new NetworkScanner();
        scanner.Scan();
        return scanner.Controllers.Cast<ControllerInfo>().ToList();
    }

    public static void Pin(string target)
    {
        lock (Gate)
        {
            _pinnedTarget = target;
            DisposeController();
        }
    }

    /// <summary>Runs an action against the controller, reconnecting once on failure.</summary>
    public static T Use<T>(Func<Controller, T> action)
    {
        lock (Gate)
        {
            try
            {
                return action(GetController());
            }
            catch (Exception ex) when (ex is not RobotToolException)
            {
                // One transparent retry on a fresh connection (VC restarts, drops).
                DisposeController();
                return action(GetController());
            }
        }
    }

    /// <summary>Blocks writes on a real controller unless the caller confirmed.</summary>
    public static void EnsureWriteAllowed(Controller c, bool confirm, string operation)
    {
        if (!c.IsVirtual && !confirm)
            throw new RobotToolException(
                $"REFUSED: '{operation}' targets a REAL robot ({c.SystemName}). " +
                "Ask the user for explicit confirmation of this specific action, " +
                "then retry with confirm=true.");
    }

    private static Controller GetController()
    {
        if (_controller is { Connected: true })
            return _controller;

        DisposeController();

        var found = Discover();
        string? want = _pinnedTarget ?? Environment.GetEnvironmentVariable("ABBCTL_CONTROLLER");

        ControllerInfo info;
        if (!string.IsNullOrEmpty(want))
        {
            info = found.FirstOrDefault(ci =>
                       string.Equals(ci.IPAddress.ToString(), want, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ci.SystemName, want, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ci.ControllerName, want, StringComparison.OrdinalIgnoreCase))
                   ?? throw new RobotToolException(
                       $"controller '{want}' not found on the network; use robot_scan to list controllers");
        }
        else if (found.Count == 1)
        {
            info = found[0];
        }
        else if (found.Count == 0)
        {
            throw new RobotToolException(
                "no controllers found. For a virtual controller the user must start the station in RobotStudio.");
        }
        else
        {
            string names = string.Join(", ", found.Select(ci => $"{ci.SystemName} ({ci.IPAddress})"));
            throw new RobotToolException($"multiple controllers found; pin one with robot_connect: {names}");
        }

        _controller = Controller.Connect(info, ConnectionType.Standalone);
        _controller.Logon(UserInfo.DefaultUser);
        return _controller;
    }

    private static void DisposeController()
    {
        if (_controller != null)
        {
            try { _controller.Logoff(); } catch { }
            try { _controller.Dispose(); } catch { }
            _controller = null;
        }
    }
}

/// <summary>Deliberate tool-level failure whose message is meant for the model.</summary>
internal sealed class RobotToolException : Exception
{
    public RobotToolException(string message) : base(message) { }
}
