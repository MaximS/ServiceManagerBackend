using Systemd.DBus;
using Tmds.DBus.Protocol;

namespace SystemdServiceController;

public enum UnitJob
{
    Start,
    Stop,
    Restart,
    Reload,
    TryRestart,
    ReloadOrRestart,
    ReloadOrTryRestart,
}

public sealed record UnitInfo(
    string Name,
    string Description,
    string LoadState,
    string ActiveState,
    string SubState,
    string Following,
    ObjectPath ObjectPath,
    uint JobId,
    string JobType,
    ObjectPath JobPath);

public sealed record UnitFile(string Name, string State);

public sealed record UnitStatus(
    string Id,
    string Description,
    string LoadState,
    string ActiveState,
    string SubState,
    string UnitFileState,
    string FragmentPath,
    (uint Id, ObjectPath Path)? Job,
    ulong StateChangeTimestampMicroseconds);

public sealed record JobInfo(
    uint Id,
    string Unit,
    string JobType,
    string State,
    ObjectPath JobPath,
    ObjectPath UnitPath);

public sealed record JobOutcome(
    uint JobId,
    ObjectPath JobPath,
    string? Result);

public sealed record UnitState(
    string? LoadState,
    string? ActiveState,
    string? SubState,
    string? UnitFileState,
    (uint Id, ObjectPath Path)? Job);

public static class UnitName
{
    public const string DefaultSuffix = ".service";

    public static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Contains('.') ? name : name + DefaultSuffix;
    }
}

public sealed class SystemdClient : IAsyncDisposable
{
    public const string ServiceName = "org.freedesktop.systemd1";
    public const string ManagerObjectPath = "/org/freedesktop/systemd1";
    public const string DefaultJobMode = "replace";
    public static readonly TimeSpan JobWaitTimeout = TimeSpan.FromMinutes(10);

    private readonly bool _userMode;
    private readonly DBusConnection _connection;
    private readonly object _subscriptionSync = new();
    private NameOwnerWatcher? _nameOwnerWatcher;
    private DBusService? _systemd;
    private bool _isSubscribed;
    private bool _isDisposed;

    public SystemdClient(bool userMode = false)
    {
        _userMode = userMode;

        string? address = userMode ? DBusAddress.Session : DBusAddress.System;
        if (address is null)
        {
            throw new InvalidOperationException(
                userMode
                    ? "No session D-Bus bus is available (check $XDG_RUNTIME_DIR). The user systemd " +
                      "instance requires a running session bus, e.g. a login session started by systemd-logind."
                    : "No system D-Bus bus is available. This tool must run on a Linux host with a running system bus.");
        }

        var options = new DBusConnectionOptions(address)
        {
            OnException = context =>
                Console.Error.WriteLine($"[D-Bus] {context.Source}: {context.Exception.Message}"),
        };

        _connection = new DBusConnection(options);
    }

    /// <summary>True when targeting the per-user systemd instance (<c>systemctl --user</c> equivalent).</summary>
    public bool IsUserMode => _userMode;

    public DBusConnection Connection => _connection;

    public NameOwnerWatcher? NameOwnerWatcher => _nameOwnerWatcher;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await _connection.ConnectAsync().ConfigureAwait(false);

        _nameOwnerWatcher = await _connection.WatchNameOwnerAsync(ServiceName).ConfigureAwait(false);
        string ownerIdentifier = await _nameOwnerWatcher.WaitForOwnerAsync(cancellationToken).ConfigureAwait(false);
        _systemd = new DBusService(_connection, NameOwnerWatcher.GetOwnerBusName(ownerIdentifier));
    }

    private DBusService GetService()
        => _systemd
           ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

    private Manager GetManager()
        => GetService().CreateManager(ManagerObjectPath);

    public async ValueTask EnsureSubscribedAsync(CancellationToken cancellationToken = default)
    {
        lock (_subscriptionSync)
        {
            if (_isSubscribed)
            {
                return;
            }
        }

        await GetManager().SubscribeAsync().ConfigureAwait(false);

        lock (_subscriptionSync)
        {
            _isSubscribed = true;
        }
    }

    public Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
        => GetManager().GetVersionAsync();

    public async Task<IReadOnlyList<UnitInfo>> ListUnitsAsync(CancellationToken cancellationToken = default)
    {
        (string, string, string, string, string, string, ObjectPath, uint, string, ObjectPath)[] units =
            await GetManager().ListUnitsAsync().ConfigureAwait(false);

        return units
            .Select(u => new UnitInfo(u.Item1, u.Item2, u.Item3, u.Item4, u.Item5, u.Item6, u.Item7, u.Item8, u.Item9, u.Item10))
            .ToList();
    }

    public async Task<IReadOnlyList<UnitFile>> ListUnitFilesAsync(CancellationToken cancellationToken = default)
    {
        (string, string)[] files = await GetManager().ListUnitFilesAsync().ConfigureAwait(false);
        return files.Select(f => new UnitFile(f.Item1, f.Item2)).ToList();
    }

    public async Task<UnitStatus> GetUnitStatusAsync(string unit, CancellationToken cancellationToken = default)
    {
        string name = UnitName.Normalize(unit);
        ObjectPath path = await GetUnitPathAsync(name).ConfigureAwait(false);

        var unitProxy = GetService().CreateUnit(path);
        UnitProperties props = await unitProxy.GetPropertiesAsync().ConfigureAwait(false);

        return new UnitStatus(
            Id: props.Id,
            Description: props.Description,
            LoadState: props.LoadState,
            ActiveState: props.ActiveState,
            SubState: props.SubState,
            UnitFileState: props.UnitFileState,
            FragmentPath: props.FragmentPath,
            Job: props.Job.Item1 == 0 ? null : (props.Job.Item1, props.Job.Item2),
            StateChangeTimestampMicroseconds: props.StateChangeTimestamp);
    }

    private async Task<ObjectPath> GetUnitPathAsync(string unitName)
    {
        Manager manager = GetManager();
        try
        {
            return await manager.GetUnitAsync(unitName).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            return await manager.LoadUnitAsync(unitName).ConfigureAwait(false);
        }
    }

    public async Task<JobOutcome> EnqueueUnitJobAsync(
        string unit,
        UnitJob job,
        bool waitForCompletion,
        CancellationToken cancellationToken = default)
    {
        string name = UnitName.Normalize(unit);
        Manager manager = GetManager();

        TaskCompletionSource<(uint Id, string Result)>? completion = waitForCompletion
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        var pendingJob = new PendingJob();
        IDisposable? observer = null;

        if (completion is not null)
        {
            await EnsureSubscribedAsync(cancellationToken).ConfigureAwait(false);

            observer = await manager.WatchJobRemovedAsync(args =>
            {
                if (pendingJob.Is(args.Job))
                {
                    completion.TrySetResult((args.Id, args.Result));
                }
            }, emitOnCapturedContext: false).ConfigureAwait(false);
        }

        try
        {
            ObjectPath jobPath = job switch
            {
                UnitJob.Start => await manager.StartUnitAsync(name, DefaultJobMode).ConfigureAwait(false),
                UnitJob.Stop => await manager.StopUnitAsync(name, DefaultJobMode).ConfigureAwait(false),
                UnitJob.Restart => await manager.RestartUnitAsync(name, DefaultJobMode).ConfigureAwait(false),
                UnitJob.Reload => await manager.ReloadUnitAsync(name, DefaultJobMode).ConfigureAwait(false),
                UnitJob.TryRestart => await manager.TryRestartUnitAsync(name, DefaultJobMode).ConfigureAwait(false),
                UnitJob.ReloadOrRestart => await manager.ReloadOrRestartUnitAsync(name, DefaultJobMode).ConfigureAwait(false),
                UnitJob.ReloadOrTryRestart => await manager.ReloadOrTryRestartUnitAsync(name, DefaultJobMode).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(job), job, null),
            };

            pendingJob.Set(jobPath);
            uint jobId = ParseJobId(jobPath);

            if (completion is null)
            {
                return new JobOutcome(jobId, jobPath, Result: null);
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(JobWaitTimeout);

            (uint Id, string Result) result = await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new JobOutcome(result.Id, jobPath, result.Result);
        }
        finally
        {
            observer?.Dispose();
        }
    }

    public async Task<(bool CarriesInstallInfo, (string Kind, string Symlink, string Destination)[] Changes)> EnableUnitsAsync(
        string[] units,
        bool runtime,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(units);

        (bool, (string, string, string)[]) result = await GetManager()
            .EnableUnitFilesAsync(units.Select(UnitName.Normalize).ToArray(), runtime, force)
            .ConfigureAwait(false);

        (string Kind, string Symlink, string Destination)[] changes =
            result.Item2.Select(c => (c.Item1, c.Item2, c.Item3)).ToArray();

        return (result.Item1, changes);
    }

    public async Task<(string Kind, string Symlink, string Destination)[]> DisableUnitsAsync(
        string[] units,
        bool runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(units);

        (string, string, string)[] changes = await GetManager()
            .DisableUnitFilesAsync(units.Select(UnitName.Normalize).ToArray(), runtime)
            .ConfigureAwait(false);

        return changes.Select(c => (c.Item1, c.Item2, c.Item3)).ToArray();
    }

    public Task<string> GetUnitFileStateAsync(string unit, CancellationToken cancellationToken = default)
        => GetManager().GetUnitFileStateAsync(UnitName.Normalize(unit));

    public Task ResetFailedAsync(string? unit, CancellationToken cancellationToken = default)
        => unit is null
            ? GetManager().ResetFailedAsync()
            : GetManager().ResetFailedUnitAsync(UnitName.Normalize(unit));

    public async Task<IReadOnlyList<JobInfo>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        (uint, string, string, string, ObjectPath, ObjectPath)[] jobs = await GetManager().ListJobsAsync().ConfigureAwait(false);

        return jobs
            .Select(j => new JobInfo(j.Item1, j.Item2, j.Item3, j.Item4, j.Item5, j.Item6))
            .ToList();
    }

    public async ValueTask<IDisposable> WatchUnitAsync(
        string unit,
        Action<UnitState> onStateChanged,
        Action<(uint JobId, string Unit, string Result)>? onJobRemoved = null,
        CancellationToken cancellationToken = default)
    {
        string name = UnitName.Normalize(unit);
        ArgumentNullException.ThrowIfNull(onStateChanged);

        await EnsureSubscribedAsync(cancellationToken).ConfigureAwait(false);

        ObjectPath path = await GetUnitPathAsync(name).ConfigureAwait(false);
        var unitProxy = GetService().CreateUnit(path);

        IDisposable propertiesObserver = await unitProxy.WatchPropertiesChangedAsync(changed =>
        {
            onStateChanged(new UnitState(
                LoadState: changed.LoadState,
                ActiveState: changed.ActiveState,
                SubState: changed.SubState,
                UnitFileState: changed.UnitFileState,
                Job: changed.Job));
        }, emitOnCapturedContext: false).ConfigureAwait(false);

        IDisposable? jobObserver = onJobRemoved is null
            ? null
            : await GetManager().WatchJobRemovedAsync(args =>
            {
                if (args.Unit == name)
                {
                    onJobRemoved((args.Id, args.Unit, args.Result));
                }
            }, emitOnCapturedContext: false).ConfigureAwait(false);

        return new UnitWatch(propertiesObserver, jobObserver);
    }

    public static uint ParseJobId(ObjectPath jobPath)
    {
        string text = jobPath.ToString();
        const string prefix = "/org/freedesktop/systemd1/job/";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !uint.TryParse(text[prefix.Length..], out uint id))
        {
            throw new ArgumentException($"Not a job object path: {text}", nameof(jobPath));
        }

        return id;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_isSubscribed)
        {
            try
            {
                await GetManager().UnsubscribeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort: the connection is going away anyway.
            }
        }

        _nameOwnerWatcher?.Dispose();
        _connection.Dispose();

        await Task.CompletedTask;
    }

    private sealed class PendingJob
    {
        private readonly object _sync = new();
        private ObjectPath? _path;

        public void Set(ObjectPath path)
        {
            lock (_sync)
            {
                _path = path;
            }
        }

        public bool Is(ObjectPath candidate)
        {
            lock (_sync)
            {
                return _path.HasValue && _path.Value.ToString() == candidate.ToString();
            }
        }
    }

    private sealed class UnitWatch : IDisposable
    {
        private readonly IDisposable _propertiesObserver;
        private readonly IDisposable? _jobObserver;

        public UnitWatch(IDisposable propertiesObserver, IDisposable? jobObserver)
        {
            _propertiesObserver = propertiesObserver;
            _jobObserver = jobObserver;
        }

        public void Dispose()
        {
            _propertiesObserver.Dispose();
            _jobObserver?.Dispose();
        }
    }
}
