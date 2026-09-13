using Tmds.DBus.Protocol;

namespace SystemdServiceController;

public sealed class Cli(SystemdClient client)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0];
        string[] rest = args[1..];

        try
        {
            return await (command switch
            {
                "list" => ListUnitsAsync(rest, cancellationToken),
                "files" => ListUnitFilesAsync(rest, cancellationToken),
                "status" => ShowStatusAsync(rest, cancellationToken),
                "start" or "stop" or "restart" or "reload" or "try-restart" or "reload-or-restart" =>
                    ControlUnitAsync(command, rest, cancellationToken),
                "enable" => EnableUnitsAsync(rest, cancellationToken),
                "disable" => DisableUnitsAsync(rest, cancellationToken),
                "is-enabled" => ShowEnablementAsync(rest, cancellationToken),
                "reset-failed" => ResetFailedAsync(rest, cancellationToken),
                "jobs" => ListJobsAsync(cancellationToken),
                "watch" => WatchUnitAsync(rest, cancellationToken),
                "version" => ShowVersionAsync(cancellationToken),
                _ => Task.FromResult(UnknownCommand(command)),
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ex);
        }

        catch (OperationCanceledException)
        {
            return Fail("Operation cancelled.");
        }
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            SystemdServiceController - control systemd units over D-Bus (org.freedesktop.systemd1)

            Usage: SystemdServiceController [--user] <command> [args] [options]

            Global options:
              --user                 Target the per-user systemd instance
                                     (like 'systemctl --user'): connects to the
                                     session bus and manages units under
                                     ~/.config/systemd/user/. Can be placed
                                     anywhere on the command line.

            Commands:
              list [filter]                List loaded units
              files [filter]               List unit files and their enablement state
              status <unit>                Show the state of a unit
              start <unit> [--no-wait]     Start a unit (waits for the job to finish by default)
              stop <unit> [--no-wait]      Stop a unit
              restart <unit> [--no-wait]   Restart a unit
              reload <unit> [--no-wait]    Reload a unit
              try-restart <unit>           Restart only if the unit is active
              reload-or-restart <unit>     Reload if supported, otherwise restart
              enable <unit...> [--runtime] [--force]   Enable unit files
              disable <unit...> [--runtime]            Disable unit files
              is-enabled <unit>            Print the enablement state of a unit file
              reset-failed [unit]          Reset the failed state of one unit (or all units)
              jobs                         List queued jobs
              watch <unit>                 Follow state and job changes until Ctrl+C
              version                      Print the systemd version

            Notes:
              * A unit name without a suffix gets '.service' appended, like systemctl does.
              * Read-only commands work for any user. Mutating commands require the polkit
                action org.freedesktop.systemd1.manage-units / .manage-unit-files (use sudo
                if your user is not authorized).
              * With --user, the same API is used on the session bus against the
                per-user instance; its owner can manage own units without polkit auth.
            """);
    }

    private async Task<int> ShowVersionAsync(CancellationToken cancellationToken)
    {
        string version = await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine(client.IsUserMode ? $"systemd {version} (user instance)" : $"systemd {version}");
        return 0;
    }

    private async Task<int> ListUnitsAsync(string[] rest, CancellationToken cancellationToken)
    {
        string? filter = rest.Length > 0 ? rest[0] : null;
        IReadOnlyList<UnitInfo> units = await client.ListUnitsAsync(cancellationToken).ConfigureAwait(false);
        if (filter is not null)
        {
            units = units.Where(u => u.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        PrintTable(
            new[] { "UNIT", "LOAD", "ACTIVE", "SUB", "JOB" },
            units.Select(u => new[]
            {
                u.Name,
                u.LoadState,
                u.ActiveState,
                u.SubState,
                u.JobId == 0 ? "-" : $"{u.JobId} {u.JobType}",
            }).ToArray());

        return 0;
    }

    private async Task<int> ListUnitFilesAsync(string[] rest, CancellationToken cancellationToken)
    {
        string? filter = rest.Length > 0 ? rest[0] : null;
        IReadOnlyList<UnitFile> files = await client.ListUnitFilesAsync(cancellationToken).ConfigureAwait(false);
        if (filter is not null)
        {
            files = files.Where(f => f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        PrintTable(
            new[] { "UNIT FILE", "STATE" },
            files.Select(f => new[] { f.Name, f.State }).ToArray());

        return 0;
    }

    private async Task<int> ShowStatusAsync(string[] rest, CancellationToken cancellationToken)
    {
        EnsureArgs(rest, 1, "status");
        UnitStatus unit = await client.GetUnitStatusAsync(rest[0], cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"{unit.Id} - {unit.Description}");
        Console.WriteLine();
        Console.WriteLine($"   State: {unit.ActiveState} ({unit.SubState})");
        Console.WriteLine($"    Load: {unit.LoadState}");
        Console.WriteLine($"    File: {unit.UnitFileState}");
        Console.WriteLine($"  Fragment: {(unit.FragmentPath.Length > 0 ? unit.FragmentPath : "(none)")}");
        if (unit.Job is (uint id, var path))
        {
            Console.WriteLine($"     Job: {id} ({path})");
        }

        if (unit.StateChangeTimestampMicroseconds > 0)
        {
            DateTimeOffset changed = DateTimeOffset.UnixEpoch.Add(TimeSpan.FromMicroseconds((long)unit.StateChangeTimestampMicroseconds));
            Console.WriteLine($" Changed: {changed:u}");
        }

        return unit.LoadState == "not-found" ? 4 : 0;
    }

    private async Task<int> ControlUnitAsync(string command, string[] rest, CancellationToken cancellationToken)
    {
        bool noWait = rest.Contains("--no-wait");
        string[] unitArgs = rest.Where(a => a is not "--no-wait").ToArray();
        EnsureArgs(unitArgs, 1, command);

        UnitJob job = command switch
        {
            "start" => UnitJob.Start,
            "stop" => UnitJob.Stop,
            "restart" => UnitJob.Restart,
            "reload" => UnitJob.Reload,
            "try-restart" => UnitJob.TryRestart,
            "reload-or-restart" => UnitJob.ReloadOrRestart,
            _ => throw new ArgumentOutOfRangeException(),
        };

        string unitName = UnitName.Normalize(unitArgs[0]);
        JobOutcome outcome = await client.EnqueueUnitJobAsync(unitArgs[0], job, waitForCompletion: !noWait, cancellationToken).ConfigureAwait(false);

        if (outcome.Result is null)
        {
            Console.WriteLine($"Enqueued job {outcome.JobId} ({outcome.JobPath}) for {unitName}; not waiting.");
            return 0;
        }

        Console.WriteLine($"Job {outcome.JobId} for {unitName} finished: {outcome.Result}");
        return outcome.Result == "done" ? 0 : 1;
    }

    private async Task<int> EnableUnitsAsync(string[] rest, CancellationToken cancellationToken)
    {
        bool runtime = rest.Contains("--runtime");
        bool force = rest.Contains("--force");
        string[] units = rest.Where(a => a is not "--runtime" and not "--force").ToArray();
        EnsureArgs(units, 1, "enable");

        (bool CarriesInstallInfo, (string Kind, string Symlink, string Destination)[] Changes) result =
            await client.EnableUnitsAsync(units, runtime, force, cancellationToken).ConfigureAwait(false);

        if (!result.CarriesInstallInfo)
        {
            Console.WriteLine("Warning: no [Install] sections found in the specified unit files.");
        }

        foreach ((string kind, string symlink, string destination) in result.Changes)
        {
            Console.WriteLine($"{kind} {symlink} -> {destination}");
        }

        return 0;
    }

    private async Task<int> DisableUnitsAsync(string[] rest, CancellationToken cancellationToken)
    {
        bool runtime = rest.Contains("--runtime");
        string[] units = rest.Where(a => a is not "--runtime").ToArray();
        EnsureArgs(units, 1, "disable");

        (string Kind, string Symlink, string Destination)[] changes = await client.DisableUnitsAsync(units, runtime, cancellationToken).ConfigureAwait(false);
        foreach ((string kind, string symlink, _) in changes)
        {
            Console.WriteLine($"{kind} {symlink}");
        }

        return 0;
    }

    private async Task<int> ShowEnablementAsync(string[] rest, CancellationToken cancellationToken)
    {
        EnsureArgs(rest, 1, "is-enabled");
        string state = await client.GetUnitFileStateAsync(rest[0], cancellationToken).ConfigureAwait(false);
        Console.WriteLine(state);
        return state is "enabled" or "enabled-runtime" or "static" or "linked" or "linked-runtime" ? 0 : 1;
    }

    private async Task<int> ResetFailedAsync(string[] rest, CancellationToken cancellationToken)
    {
        string? unit = rest.Length > 0 ? rest[0] : null;
        await client.ResetFailedAsync(unit, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(unit is null ? "Reset failed state of all units." : $"Reset failed state of {UnitName.Normalize(unit)}.");
        return 0;
    }

    private async Task<int> ListJobsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<JobInfo> jobs = await client.ListJobsAsync(cancellationToken).ConfigureAwait(false);
        if (jobs.Count == 0)
        {
            Console.WriteLine("No queued jobs.");
            return 0;
        }

        PrintTable(
            new[] { "JOB", "UNIT", "TYPE", "STATE" },
            jobs.Select(j => new[] { j.Id.ToString(), j.Unit, j.JobType, j.State }).ToArray());

        return 0;
    }

    private async Task<int> WatchUnitAsync(string[] rest, CancellationToken cancellationToken)
    {
        EnsureArgs(rest, 1, "watch");
        string unit = UnitName.Normalize(rest[0]);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += OnCancelKeyPress;
        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancellation.Cancel();
        }

        Console.WriteLine($"Watching {unit} (Ctrl+C to stop)...");
        PrintState(await client.GetUnitStatusAsync(unit, cancellation.Token).ConfigureAwait(false), initial: true);

        IDisposable watch = await client.WatchUnitAsync(
            unit,
            state => PrintState(state),
            job => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Job {job.JobId} removed: {job.Result}"),
            cancellation.Token).ConfigureAwait(false);

        using (watch)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Console.WriteLine();
        Console.WriteLine("Stopped watching.");
        return 0;
    }

    private void PrintState(UnitStatus status, bool initial = false)
    {
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] {(initial ? "initial " : "")}load={status.LoadState} active={status.ActiveState} sub={status.SubState} file={status.UnitFileState}");
    }

    private void PrintState(UnitState state)
    {
        string load = state.LoadState ?? "-";
        string active = state.ActiveState ?? "-";
        string sub = state.SubState ?? "-";
        string file = state.UnitFileState ?? "-";
        string job = state.Job is (uint id, _) ? $" job={id}" : string.Empty;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] load={load} active={active} sub={sub} file={file}{job}");
    }

    private static void EnsureArgs(string[] args, int minimum, string command)
    {
        if (args.Length < minimum)
        {
            throw new ArgumentException($"Usage: {command} <unit> [options]. Run 'help' for usage.");
        }
    }

      private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private int Fail(Exception ex) => ex switch
    {
        DBusErrorReplyException error => FailBusError(error),
        InvalidOperationException error => Fail(error.Message),
        ArgumentException error => Fail(error.Message),
        Exception error => Fail($"{error.GetType().Name}: {error.Message}"),
    };

    private int FailBusError(DBusErrorReplyException error)
    {
        string message = error.ErrorName switch
        {
            "org.freedesktop.systemd1.UnitNotFound" =>
                $"Unit not found ({error.ErrorMessage}). Check the name with 'list' or 'files'.",
            "org.freedesktop.systemd1.UnitMasked" =>
                $"Unit is masked ({error.ErrorMessage}). Unmask it first (e.g. 'systemctl unmask').",
            "org.freedesktop.systemd1.UnitNotActive" =>
                $"Unit is not active ({error.ErrorMessage}).",
            "org.freedesktop.DBus.Error.AccessDenied"
                or "org.freedesktop.DBus.Error.InteractiveAuthorizationRequired"
                or "org.freedesktop.PolicyKit1.Error.Failed" =>
                $"Not authorized ({error.ErrorMessage}). " +
                (client.IsUserMode
                    ? "The session bus is per-user: make sure you run in the session owner's " +
                      "login session ($XDG_RUNTIME_DIR set, user systemd instance running)."
                    : "Re-run with sudo, or grant the polkit action " +
                      "org.freedesktop.systemd1.manage-units (or .manage-unit-files) to your user."),
            "org.freedesktop.DBus.Error.NoReply" =>
                $"systemd did not reply ({error.ErrorMessage}). It may be waiting for polkit authentication.",
            _ => $"D-Bus error {error.ErrorName}: {error.ErrorMessage}",
        };

        return Fail(message);
    }

    private int Fail(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    private static void PrintTable(string[] header, string[][] rows)
    {
        int[] widths = new int[header.Length];
        for (int c = 0; c < header.Length; c++)
        {
            widths[c] = header[c].Length;
            foreach (string[] row in rows)
            {
                widths[c] = Math.Max(widths[c], row[c].Length);
            }
        }

        WriteRow(header, widths);
        Console.WriteLine();
        foreach (string[] row in rows)
        {
            WriteRow(row, widths);
        }
    }

    private static void WriteRow(string[] row, int[] widths)
    {
        for (int c = 0; c < row.Length; c++)
        {
            Console.Write(row[c].PadRight(c == row.Length - 1 ? 0 : widths[c] + 2));
        }

        Console.WriteLine();
    }
}
