using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ABB.Robotics.Controllers;
using ABB.Robotics.Controllers.IOSystemDomain;
using ABB.Robotics.Controllers.MotionDomain;
using ABB.Robotics.Controllers.RapidDomain;
using ModelContextProtocol.Server;
using RapidTask = ABB.Robotics.Controllers.RapidDomain.Task;

namespace AbbCtl.Mcp;

[McpServerToolType]
public static class RobotTools
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static string ToJson(object o) => JsonSerializer.Serialize(o, JsonOpts);

    // ---------- discovery / session ----------

    [McpServerTool(Name = "robot_scan")]
    [Description("List ABB controllers reachable on the network (real and RobotStudio virtual controllers). Virtual controllers only appear while their station is running in RobotStudio.")]
    public static string Scan()
    {
        var list = RobotSession.Discover().Select(ci => new
        {
            systemName = ci.SystemName,
            ip = ci.IPAddress.ToString(),
            isVirtual = ci.IsVirtual,
            robotware = ci.Version.ToString(),
            availability = ci.Availability.ToString()
        });
        return ToJson(list);
    }

    [McpServerTool(Name = "robot_connect")]
    [Description("Pin the target controller by system name or IP for all subsequent tools. Only needed when robot_scan shows more than one controller.")]
    public static string Connect(
        [Description("Controller system name or IP address as shown by robot_scan")] string target)
    {
        RobotSession.Pin(target);
        return RobotSession.Use(c => ToJson(new { connected = c.SystemName, isVirtual = c.IsVirtual }));
    }

    [McpServerTool(Name = "robot_info")]
    [Description("Controller status: system name, virtual or REAL, operating mode, motors state, RAPID execution status, speed ratio, and program/motion pointer per task. Call this before any write to know whether the target is a real robot.")]
    public static string Info() => RobotSession.Use(c =>
    {
        var tasks = c.Rapid.GetTasks().Select(t => new
        {
            task = t.Name,
            taskType = t.TaskType.ToString(),
            motion = t.Motion,
            executionStatus = t.ExecutionStatus.ToString(),
            programPointer = DescribePointer(() => t.ProgramPointer),
            motionPointer = DescribePointer(() => t.MotionPointer)
        });
        return ToJson(new
        {
            systemName = c.SystemName,
            isVirtual = c.IsVirtual,
            robotware = c.RobotWareVersion.ToString(),
            operatingMode = c.OperatingMode.ToString(),
            state = c.State.ToString(),
            executionStatus = c.Rapid.ExecutionStatus.ToString(),
            speedRatioPercent = c.MotionSystem.SpeedRatio,
            tasks
        });
    });

    private static string DescribePointer(Func<ProgramPosition> get)
    {
        try
        {
            var p = get();
            return p == null ? "(not set)" : $"{p.Module}/{p.Routine}:{p.Range.Begin.Row}";
        }
        catch { return "(not set)"; }
    }

    // ---------- program ----------

    [McpServerTool(Name = "robot_program_tree")]
    [Description("List RAPID tasks and the modules loaded in each (name and whether it is a system module).")]
    public static string ProgramTree() => RobotSession.Use(c =>
        ToJson(c.Rapid.GetTasks().Select(t => new
        {
            task = t.Name,
            modules = t.GetModules().Select(m => new { name = m.Name, isSystem = m.IsSystem })
        })));

    [McpServerTool(Name = "robot_get_module_source")]
    [Description("Return the full RAPID source code of a module from program memory. Use it to inspect the program and as the base (and rollback copy) for edits before robot_load_module.")]
    public static string GetModuleSource(
        [Description("Module name, e.g. MainModule")] string module,
        [Description("Task name; omit for the only/default task (T_ROB1)")] string? task = null)
        => RobotSession.Use(c =>
        {
            var t = ResolveTask(c, task);
            return FetchModuleText(c, t, module);
        });

    [McpServerTool(Name = "robot_load_module")]
    [Description("Replace (or add) a RAPID module in program memory from source text. Requires stopped execution (robot_stop first). On success run robot_reset_pp then robot_start to run the new code. IMPORTANT: fetch the current source with robot_get_module_source first and keep it as rollback; if this load fails due to a syntax error the old module may already be gone, so reload the rollback copy.")]
    public static string LoadModule(
        [Description("Complete RAPID module source, starting with MODULE <name> and ending with ENDMODULE")] string source,
        [Description("Module name (must match the MODULE declaration), e.g. MainModule")] string moduleName,
        [Description("Task name; omit for the only/default task")] string? task = null,
        [Description("Required true when the controller is a real robot (after user approval)")] bool confirm = false)
        => RobotSession.Use(c =>
        {
            RobotSession.EnsureWriteAllowed(c, confirm, "load module " + moduleName);
            if (c.Rapid.ExecutionStatus == ExecutionStatus.Running)
                throw new RobotToolException("RAPID execution is running; call robot_stop first");

            var t = ResolveTask(c, task);
            var fs = c.FileSystem;
            string remoteName = $"abbctl_mcp_{moduleName}.mod";
            string localPath = Path.Combine(Path.GetTempPath(), remoteName);
            File.WriteAllText(localPath, source);
            try
            {
                fs.PutFile(localPath, remoteName, true);
                bool ok;
                using (Mastership.Request(c))
                    ok = t.LoadModuleFromFile(fs.RemoteDirectory + "/" + remoteName, RapidLoadMode.Replace);
                if (!ok)
                    throw new RobotToolException(
                        "controller rejected the module (RAPID syntax error?). Check robot_log and reload the rollback copy.");
                return ToJson(new { loaded = moduleName, task = t.Name, next = "robot_reset_pp then robot_start" });
            }
            finally
            {
                try { fs.RemoveFile(remoteName); } catch { }
                try { File.Delete(localPath); } catch { }
            }
        });

    [McpServerTool(Name = "robot_start")]
    [Description("Start RAPID execution (requires Auto mode and motors on). On a REAL robot this makes the physical robot move.")]
    public static string Start(
        [Description("'once' runs a single cycle, 'forever' loops continuously")] string cycle = "once",
        [Description("Required true when the controller is a real robot (after user approval)")] bool confirm = false)
        => RobotSession.Use(c =>
        {
            RobotSession.EnsureWriteAllowed(c, confirm, "start program execution");
            if (c.OperatingMode != ControllerOperatingMode.Auto)
                throw new RobotToolException($"controller is in {c.OperatingMode} mode; remote start requires Auto");

            var cyc = cycle.Equals("forever", StringComparison.OrdinalIgnoreCase)
                ? ExecutionCycle.Forever : ExecutionCycle.Once;
            StartResult result;
            using (Mastership.Request(c))
                result = c.Rapid.Start(RegainMode.Continue, ExecutionMode.Continuous, cyc, StartCheck.CallChain);
            if (result != StartResult.Ok)
                throw new RobotToolException("start failed: " + result);
            return ToJson(new { started = true, cycle = cyc.ToString() });
        });

    [McpServerTool(Name = "robot_stop")]
    [Description("Stop RAPID execution immediately. Always safe; never requires confirmation.")]
    public static string Stop() => RobotSession.Use(c =>
    {
        using (Mastership.Request(c))
            c.Rapid.Stop(StopMode.Immediate);
        return ToJson(new { stopped = true });
    });

    [McpServerTool(Name = "robot_reset_pp")]
    [Description("Reset the program pointer to the main routine (needed after robot_load_module, before robot_start).")]
    public static string ResetPp(
        [Description("Task name; omit for the only/default task")] string? task = null)
        => RobotSession.Use(c =>
        {
            var t = ResolveTask(c, task);
            using (Mastership.Request(c))
                t.ResetProgramPointer();
            return ToJson(new { task = t.Name, programPointer = "main" });
        });

    // ---------- rapid data ----------

    [McpServerTool(Name = "robot_rapid_get")]
    [Description("Read a RAPID variable/persistent. Values use RAPID literal syntax (e.g. robtarget as [[x,y,z],[q1..q4],...]).")]
    public static string RapidGet(
        [Description("Task, e.g. T_ROB1")] string task,
        [Description("Module, e.g. MainModule")] string module,
        [Description("Symbol name")] string symbol)
        => RobotSession.Use(c =>
        {
            using var rd = c.Rapid.GetRapidData(task, module, symbol);
            return ToJson(new { task, module, symbol, type = rd.RapidType, value = rd.Value.ToString() });
        });

    [McpServerTool(Name = "robot_rapid_set")]
    [Description("Write a RAPID variable/persistent using RAPID literal syntax. Works while the program is running — the standard way to tune parameters without stopping.")]
    public static string RapidSet(
        [Description("Task, e.g. T_ROB1")] string task,
        [Description("Module, e.g. MainModule")] string module,
        [Description("Symbol name")] string symbol,
        [Description("New value in RAPID literal syntax, e.g. '10' or 'TRUE'")] string value,
        [Description("Required true when the controller is a real robot (after user approval)")] bool confirm = false)
        => RobotSession.Use(c =>
        {
            RobotSession.EnsureWriteAllowed(c, confirm, $"write RAPID data {symbol}");
            using var rd = c.Rapid.GetRapidData(task, module, symbol);
            using (Mastership.Request(c))
            {
                var v = rd.Value;
                v.FillFromString(value);
                rd.Value = v;
            }
            return ToJson(new { task, module, symbol, value = rd.Value.ToString() });
        });

    // ---------- I/O ----------

    [McpServerTool(Name = "robot_io_list")]
    [Description("List I/O signals with type and current value, optionally filtered by name substring.")]
    public static string IoList(
        [Description("Case-insensitive substring filter, e.g. 'GARRA'; omit for all")] string? filter = null)
        => RobotSession.Use(c =>
        {
            var rows = c.IOSystem.GetSignals(IOFilterTypes.All).Cast<Signal>()
                .Where(s => filter == null || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(s => new { name = s.Name, type = s.Type.ToString(), value = s.Value });
            return ToJson(rows);
        });

    [McpServerTool(Name = "robot_io_get")]
    [Description("Read the current value of one I/O signal.")]
    public static string IoGet([Description("Signal name")] string signal)
        => RobotSession.Use(c =>
        {
            var s = c.IOSystem.GetSignal(signal) ?? throw new RobotToolException($"signal '{signal}' not found");
            return ToJson(new { name = s.Name, type = s.Type.ToString(), value = s.Value });
        });

    [McpServerTool(Name = "robot_io_set")]
    [Description("Write an I/O signal. Fails if the signal's EIO Access Level does not allow remote clients (controller configuration, not a tool problem).")]
    public static string IoSet(
        [Description("Signal name")] string signal,
        [Description("Value: 0/1 for digital, numeric for analog/group")] float value,
        [Description("Required true when the controller is a real robot (after user approval)")] bool confirm = false)
        => RobotSession.Use(c =>
        {
            RobotSession.EnsureWriteAllowed(c, confirm, $"write signal {signal}");
            var s = c.IOSystem.GetSignal(signal) ?? throw new RobotToolException($"signal '{signal}' not found");
            try
            {
                s.Value = value;
            }
            catch (Exception ex) when (ex.Message.Contains("safety access restriction"))
            {
                throw new RobotToolException(
                    $"controller rejected the write: signal '{signal}' needs Access Level ALL in the EIO configuration.");
            }
            return ToJson(new { name = s.Name, value = s.Value });
        });

    // ---------- motion / speed ----------

    [McpServerTool(Name = "robot_position")]
    [Description("Current robot position for every mechanical unit: cartesian robtarget (world) and joint values.")]
    public static string Position() => RobotSession.Use(c =>
        ToJson(c.MotionSystem.MechanicalUnits.Cast<MechanicalUnit>().Select(u => new
        {
            unit = u.Name,
            robtarget = SafeCall(() => u.GetPosition(CoordinateSystemType.World).ToString()),
            joints = SafeCall(() => u.GetPosition().ToString())
        })));

    private static string SafeCall(Func<string> f)
    {
        try { return f(); } catch (Exception ex) { return "(unavailable: " + ex.Message + ")"; }
    }

    [McpServerTool(Name = "robot_set_speed")]
    [Description("Set the controller speed ratio in percent (0-100). Affects all motion immediately, even while running. Use a low value (e.g. 25) for first runs on a real robot.")]
    public static string SetSpeed(
        [Description("Speed ratio percent, 0-100")] int value,
        [Description("Required true when the controller is a real robot (after user approval)")] bool confirm = false)
        => RobotSession.Use(c =>
        {
            if (value is < 0 or > 100) throw new RobotToolException("speed must be 0-100");
            RobotSession.EnsureWriteAllowed(c, confirm, $"set speed to {value}%");
            using (Mastership.Request(c))
                c.MotionSystem.SpeedRatio = value;
            return ToJson(new { speedRatioPercent = c.MotionSystem.SpeedRatio });
        });

    // ---------- events / log ----------

    [McpServerTool(Name = "robot_log")]
    [Description("Most recent controller event log messages (errors, warnings, info), newest first. Check after any failed operation — the controller-side reason is here.")]
    public static string Log(
        [Description("Number of messages")] int count = 20)
        => RobotSession.Use(c =>
        {
            var all = new List<ABB.Robotics.Controllers.EventLogDomain.EventLogMessage>();
            foreach (var cat in c.EventLog.GetCategories())
                foreach (ABB.Robotics.Controllers.EventLogDomain.EventLogMessage m in cat.Messages)
                    all.Add(m);
            return ToJson(all.OrderByDescending(m => m.Timestamp).Take(count).Select(m => new
            {
                timestamp = m.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                type = m.Type.ToString(),
                code = $"{m.CategoryId}-{m.Number}",
                title = m.Title
            }));
        });

    [McpServerTool(Name = "robot_wait_signal")]
    [Description("Block until an I/O signal reaches a value (event-driven, no polling). Returns immediately if it already matches. Use this instead of calling robot_io_get in a loop.")]
    public static string WaitSignal(
        [Description("Signal name")] string signal,
        [Description("Value to wait for, e.g. 1")] float untilValue,
        [Description("Max seconds to wait (1-600)")] int timeoutSeconds = 60)
        => RobotSession.Use(c =>
        {
            timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 600);
            var s = c.IOSystem.GetSignal(signal) ?? throw new RobotToolException($"signal '{signal}' not found");
            if (Math.Abs(s.Value - untilValue) < 1e-6)
                return ToJson(new { name = s.Name, value = s.Value, waitedSeconds = 0.0 });

            var tcs = new TaskCompletionSource<float>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, SignalChangedEventArgs e)
            {
                if (Math.Abs(e.NewSignalState.Value - untilValue) < 1e-6)
                    tcs.TrySetResult(e.NewSignalState.Value);
            }
            s.Changed += Handler;
            try
            {
                var started = DateTime.UtcNow;
                if (!tcs.Task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                    throw new RobotToolException(
                        $"timeout: {signal} did not reach {untilValue} within {timeoutSeconds} s (current: {s.Value})");
                double waited = (DateTime.UtcNow - started).TotalSeconds;
                return ToJson(new { name = s.Name, value = tcs.Task.Result, waitedSeconds = Math.Round(waited, 3) });
            }
            finally
            {
                s.Changed -= Handler;
            }
        });

    [McpServerTool(Name = "robot_wait_execution")]
    [Description("Block until RAPID execution reaches a state ('running' or 'stopped'), e.g. to wait for a cycle started with cycle='once' to finish. Event-driven; returns immediately if already there.")]
    public static string WaitExecution(
        [Description("'running' or 'stopped'")] string until,
        [Description("Max seconds to wait (1-600)")] int timeoutSeconds = 120)
        => RobotSession.Use(c =>
        {
            timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 600);
            bool wantRunning = until.Equals("running", StringComparison.OrdinalIgnoreCase);
            bool Matches(ExecutionStatus st) => (st == ExecutionStatus.Running) == wantRunning;

            if (Matches(c.Rapid.ExecutionStatus))
                return ToJson(new { executionStatus = c.Rapid.ExecutionStatus.ToString(), waitedSeconds = 0.0 });

            var tcs = new TaskCompletionSource<ExecutionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, ExecutionStatusChangedEventArgs e)
            {
                if (Matches(e.Status)) tcs.TrySetResult(e.Status);
            }
            c.Rapid.ExecutionStatusChanged += Handler;
            try
            {
                var started = DateTime.UtcNow;
                if (!tcs.Task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                    throw new RobotToolException(
                        $"timeout: execution did not become {until} within {timeoutSeconds} s (current: {c.Rapid.ExecutionStatus})");
                double waited = (DateTime.UtcNow - started).TotalSeconds;
                return ToJson(new { executionStatus = tcs.Task.Result.ToString(), waitedSeconds = Math.Round(waited, 3) });
            }
            finally
            {
                c.Rapid.ExecutionStatusChanged -= Handler;
            }
        });

    // ---------- per-task control ----------

    [McpServerTool(Name = "robot_task_control")]
    [Description("Start or stop ONE RAPID task individually (others keep running). Note: SEMISTATIC/STATIC background tasks are auto-restarted by the system supervisor after a stop, and stopping one whose TrustLevel is not NoSafety halts the whole system.")]
    public static string TaskControl(
        [Description("'start' or 'stop'")] string action,
        [Description("Task name, e.g. T_ROB1 or T_BACK")] string task,
        [Description("Required true when the controller is a real robot (after user approval)")] bool confirm = false)
        => RobotSession.Use(c =>
        {
            var t = ResolveTask(c, task);
            bool start = action.Equals("start", StringComparison.OrdinalIgnoreCase);
            if (start) RobotSession.EnsureWriteAllowed(c, confirm, "start task " + t.Name);

            using (Mastership.Request(c))
            {
                if (start)
                {
                    var r = t.Start();
                    if (r != StartResult.Ok && t.ExecutionStatus != TaskExecutionStatus.Running)
                        throw new RobotToolException($"start of task {t.Name} failed: {r}");
                }
                else
                {
                    t.Stop(StopMode.Immediate);
                }
            }
            return ToJson(new { task = t.Name, action, executionStatus = t.ExecutionStatus.ToString() });
        });

    // ---------- configuration ----------

    [McpServerTool(Name = "robot_cfg_read")]
    [Description("Browse/read the controller configuration database. No args: list domains (SYS, EIO, MOC, ...). With domain: list types. With domain+type: list instances. With domain+type+instance: all attributes and values.")]
    public static string CfgRead(
        [Description("Domain name, e.g. EIO or SYS")] string? domain = null,
        [Description("Type name, e.g. EIO_SIGNAL or CAB_TASKS")] string? type = null,
        [Description("Instance name, e.g. a signal or task name")] string? instance = null)
        => RobotSession.Use(c =>
        {
            if (domain == null)
                return ToJson(c.Configuration.Domains.Select(d => d.Name));
            var dom = c.Configuration.Domains.FirstOrDefault(d =>
                          d.Name.Equals(domain, StringComparison.OrdinalIgnoreCase))
                      ?? throw new RobotToolException($"domain '{domain}' not found");
            if (type == null)
                return ToJson(dom.Types.Select(t => t.Name));
            var ty = dom.Types.FirstOrDefault(t => t.Name.Equals(type, StringComparison.OrdinalIgnoreCase))
                     ?? throw new RobotToolException($"type '{type}' not found in {dom.Name}");
            if (instance == null)
                return ToJson(ty.GetInstances().Select(i => i.Name));
            var inst = ty.GetInstance(instance)
                       ?? throw new RobotToolException($"instance '{instance}' not found");
            var attrs = new Dictionary<string, string?>();
            foreach (ABB.Robotics.Controllers.ConfigurationDomain.Attribute a in ty.Attributes)
            {
                try { attrs[a.Name] = inst.GetAttribute(a.Name)?.ToString(); }
                catch { attrs[a.Name] = "(unreadable)"; }
            }
            return ToJson(attrs);
        });

    [McpServerTool(Name = "robot_cfg_write")]
    [Description("Modify the configuration database: op='set' changes one attribute of an existing instance; op='create' creates an instance (attributes as 'Name=Value' strings); op='delete' removes one. Changes ONLY take effect after robot_restart. DANGER: creating a task with Type=SEMISTATIC/STATIC whose Entry routine does not exist puts the controller in unrecoverable-remotely SYSFAIL at the next restart; create tasks as NORMAL, load their program, then set Type=SEMISTATIC (forceSemistaticTask overrides this guard only when the program already exists).")]
    public static string CfgWrite(
        [Description("'set', 'create' or 'delete'")] string op,
        [Description("Domain, e.g. EIO or SYS")] string domain,
        [Description("Type, e.g. EIO_SIGNAL or CAB_TASKS")] string type,
        [Description("Instance name")] string instance,
        [Description("For set: attribute name. For create: ignored")] string? attribute = null,
        [Description("For set: new value. For create: array of 'Name=Value' pairs")] string[]? values = null,
        [Description("Required true when the controller is a real robot (after user approval)")] bool confirm = false,
        [Description("Allow creating a SEMISTATIC/STATIC task directly (only if its program already exists)")] bool forceSemistaticTask = false)
        => RobotSession.Use(c =>
        {
            RobotSession.EnsureWriteAllowed(c, confirm, $"cfg {op} {domain}/{type}/{instance}");
            var dom = c.Configuration.Domains.FirstOrDefault(d =>
                          d.Name.Equals(domain, StringComparison.OrdinalIgnoreCase))
                      ?? throw new RobotToolException($"domain '{domain}' not found");
            var ty = dom.Types.FirstOrDefault(t => t.Name.Equals(type, StringComparison.OrdinalIgnoreCase))
                     ?? throw new RobotToolException($"type '{type}' not found in {dom.Name}");

            switch (op.ToLowerInvariant())
            {
                case "set":
                    if (attribute == null || values is not { Length: > 0 })
                        throw new RobotToolException("op='set' needs attribute and values=[newValue]");
                    var instSet = ty.GetInstance(instance)
                                  ?? throw new RobotToolException($"instance '{instance}' not found");
                    using (Mastership.Request(c))
                        instSet.SetAttribute(attribute, values[0]);
                    break;

                case "create":
                    var pairs = values ?? Array.Empty<string>();
                    bool risky = domain.Equals("SYS", StringComparison.OrdinalIgnoreCase) &&
                        type.Equals("CAB_TASKS", StringComparison.OrdinalIgnoreCase) &&
                        pairs.Any(p => p.StartsWith("Type=", StringComparison.OrdinalIgnoreCase) &&
                            (p.EndsWith("SEMISTATIC", StringComparison.OrdinalIgnoreCase) ||
                             p.EndsWith("STATIC", StringComparison.OrdinalIgnoreCase)));
                    if (risky && !forceSemistaticTask)
                        throw new RobotToolException(
                            "REFUSED: create the task with Type=NORMAL, robot_restart, load its program " +
                            "(robot_load_module), then set Type=SEMISTATIC and restart again. A background " +
                            "task without its Entry routine causes SYSFAIL at boot, unrecoverable remotely.");
                    using (Mastership.Request(c))
                    {
                        var created = ty.Create(instance);
                        foreach (var pair in pairs)
                        {
                            int eq = pair.IndexOf('=');
                            if (eq <= 0) throw new RobotToolException($"attribute must be Name=Value, got '{pair}'");
                            created.SetAttribute(pair[..eq], pair[(eq + 1)..]);
                        }
                    }
                    break;

                case "delete":
                    var instDel = ty.GetInstance(instance)
                                  ?? throw new RobotToolException($"instance '{instance}' not found");
                    using (Mastership.Request(c))
                        instDel.Delete();
                    break;

                default:
                    throw new RobotToolException("op must be 'set', 'create' or 'delete'");
            }
            return ToJson(new { op, path = $"{domain}/{type}/{instance}", note = "restart required (robot_restart)" });
        });

    [McpServerTool(Name = "robot_io_create")]
    [Description("Create a new I/O signal (EIO_SIGNAL config instance). Without a device it is a memory ('virtual') signal. Access='All' makes it writable by remote clients. Takes effect only after robot_restart.")]
    public static string IoCreate(
        [Description("Signal name")] string name,
        [Description("DI, DO, AI, AO, GI or GO")] string signalType,
        [Description("Access level; 'All' allows remote writes")] string access = "All",
        [Description("Required true when the controller is a real robot (after user approval)")] bool confirm = false)
        => RobotSession.Use(c =>
        {
            RobotSession.EnsureWriteAllowed(c, confirm, "create signal " + name);
            var eio = c.Configuration.Domains.First(d => d.Name == "EIO");
            var ty = eio.Types.First(t => t.Name == "EIO_SIGNAL");
            using (Mastership.Request(c))
            {
                var inst = ty.Create(name);
                inst.SetAttribute("SignalType", signalType.ToUpperInvariant());
                inst.SetAttribute("Access", access);
            }
            return ToJson(new { created = name, signalType = signalType.ToUpperInvariant(), access,
                                note = "restart required (robot_restart)" });
        });

    [McpServerTool(Name = "robot_restart")]
    [Description("Warm start the controller (required for configuration changes to take effect) and wait for it to come back. The controller is offline for the duration (~5 s virtual, 30-60 s real); RAPID execution stops.")]
    public static string Restart(
        [Description("Required true when the controller is a real robot (after user approval)")] bool confirm = false)
    {
        string name = RobotSession.Use(c =>
        {
            RobotSession.EnsureWriteAllowed(c, confirm, "warm start");
            try
            {
                using (Mastership.Request(c))
                    c.Restart();
            }
            catch
            {
                // In SYSFAIL, mastership is rejected but a plain restart is the
                // sanctioned recovery path.
                c.Restart();
            }
            return c.SystemName;
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Thread.Sleep(5000);
        while (sw.Elapsed.TotalSeconds < 180)
        {
            try
            {
                var back = RobotSession.Discover().FirstOrDefault(ci =>
                    ci.SystemName.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    ci.Availability.ToString() == "Available");
                if (back != null)
                    return ToJson(new { restarted = name, seconds = (int)sw.Elapsed.TotalSeconds });
            }
            catch { }
            Thread.Sleep(2000);
        }
        throw new RobotToolException($"{name} did not come back within 180 s");
    }

    // ---------- helpers ----------

    private static RapidTask ResolveTask(Controller c, string? name)
    {
        var tasks = c.Rapid.GetTasks();
        if (name == null)
        {
            if (tasks.Length == 1) return tasks[0];
            return tasks.FirstOrDefault(t => t.Name == "T_ROB1")
                   ?? throw new RobotToolException("multiple tasks; specify one: " +
                       string.Join(", ", tasks.Select(t => t.Name)));
        }
        return tasks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new RobotToolException($"task '{name}' not found");
    }

    private static string FetchModuleText(Controller c, RapidTask task, string moduleName)
    {
        var module = task.GetModule(moduleName)
                     ?? throw new RobotToolException($"module '{moduleName}' not found in task {task.Name}");

        // SaveToFile takes a directory (controller-side full path) and writes
        // <ModuleName>.mod/.sys inside it; transfer/listing paths are HOME-relative.
        var fs = c.FileSystem;
        string tmpDir = "abbctl_mcp_" + Guid.NewGuid().ToString("N")[..8];
        string localPath = Path.Combine(Path.GetTempPath(), tmpDir + ".mod");

        fs.CreateDirectory(tmpDir);
        try
        {
            module.SaveToFile(fs.RemoteDirectory + "/" + tmpDir);
            var file = fs.GetFilesAndDirectories(tmpDir + "/*")
                .OfType<ABB.Robotics.Controllers.FileSystemDomain.ControllerFileInfo>()
                .FirstOrDefault()
                ?? throw new RobotToolException($"controller did not produce a file for module '{moduleName}'");
            fs.GetFile(tmpDir + "/" + file.Name, localPath, true);
            return File.ReadAllText(localPath);
        }
        finally
        {
            try { fs.RemoveDirectory(tmpDir, true); } catch { }
            try { File.Delete(localPath); } catch { }
        }
    }
}
