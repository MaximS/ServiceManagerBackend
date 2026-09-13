using Tmds.DBus.Protocol;

namespace ServiceManagerBackend.DBus;

/// <summary>
/// Thin wrapper over one D-Bus connection to the per-user systemd instance
/// (the "systemctl --user" equivalent: same API, but on the session bus).
/// Registered as a singleton in DI; a single connection serves the whole app lifetime.
/// </summary>
public sealed class SystemdClient : IAsyncDisposable
{
    public const string ServiceName = "org.freedesktop.systemd1";
    public const string ManagerObjectPath = "/org/freedesktop/systemd1";

    /// <summary>systemd job mode: a new job replaces a conflicting queued one (systemctl default).</summary>
    public const string DefaultJobMode = "replace";

    /// <summary>Job result strings. Anything that is not "done" means the job did not succeed.</summary>
    public static class JobResults
    {
        public const string Done = "done";
        public const string Failed = "failed";
        public const string Canceled = "canceled";
        public const string Timeout = "timeout";
        public const string Dependency = "dependency";
        public const string Skipped = "skipped";
    }

    private readonly ILogger<SystemdClient> _logger;
    private readonly DBusConnection _connection;
    private readonly object _connectSync = new();
    private readonly object _subscriptionSync = new();
    private Task? _connectTask;
    private NameOwnerWatcher? _nameOwnerWatcher;
    private DBusService? _systemd;
    private bool _isSubscribed;
    private bool _isDisposed;

    public SystemdClient(ILogger<SystemdClient> logger)
    {
        _logger = logger;

        // User-space only: the per-user systemd instance lives on the session bus.
        string? address = DBusAddress.Session;
        if (address is null)
        {
            throw new InvalidOperationException(
                "No session D-Bus bus is available (check $XDG_RUNTIME_DIR). The user systemd " +
                "instance requires a running session bus, e.g. a login session started by systemd-logind.");
        }

        var options = new DBusConnectionOptions(address)
        {
            // Surface connection-level problems (dropped auth, bus disconnects) in the app log.
            OnException = context =>
                _logger.LogError("[D-Bus] {Source}: {Message}", context.Source, context.Exception.Message),
        };

        _connection = new DBusConnection(options);
    }

    public DBusConnection Connection => _connection;

    /// <summary>
    /// Connects lazily on first use (idempotent). The app starts even if the session bus
    /// is not up yet; the first request surfaces the real error.
    /// </summary>
    public async ValueTask EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        Task connect;
        lock (_connectSync)
        {
            if (_connectTask is null)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                _connectTask = ConnectCoreAsync();
            }

            connect = _connectTask;
        }

        await connect.ConfigureAwait(false);
    }

    private async Task ConnectCoreAsync()
    {
        await _connection.ConnectAsync().ConfigureAwait(false);

        // Wait until systemd actually owns the bus name before talking to it.
        _nameOwnerWatcher = await _connection.WatchNameOwnerAsync(ServiceName).ConfigureAwait(false);
        string ownerIdentifier = await _nameOwnerWatcher.WaitForOwnerAsync().ConfigureAwait(false);
        _systemd = new DBusService(_connection, NameOwnerWatcher.GetOwnerBusName(ownerIdentifier));
    }

    private DBusService GetService()
        => _systemd
           ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

    private Manager GetManager()
        => GetService().CreateManager(ManagerObjectPath);

    /// <summary>
    /// Manager.Subscribe() must be called on the connection before any signals
    /// (JobRemoved, PropertiesChanged) are delivered; it is a per-connection flag.
    /// </summary>
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

    /// <summary>
    /// Enqueue a start/stop job for a unit.
    /// When <paramref name="waitForCompletion"/> is true this resolves only after the
    /// JobRemoved signal for our job arrives, or <paramref name="waitTimeout"/> elapses
    /// (OperationCanceledException with a TimeoutException cause via WaitAsync).
    /// </summary>
    public async Task<JobOutcome> EnqueueUnitJobAsync(
        string unit,
        UnitJob job,
        bool waitForCompletion,
        TimeSpan? waitTimeout = null,
        CancellationToken cancellationToken = default)
    {
        string name = UnitName.Normalize(unit);
        Manager manager = GetManager();
        TimeSpan timeout = waitTimeout ?? TimeSpan.FromMinutes(10);

        TaskCompletionSource<(uint Id, string Result)>? completion = waitForCompletion
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        var pendingJob = new PendingJob();
        IDisposable? observer = null;

        if (completion is not null)
        {
            // Both prerequisites must be in place BEFORE the Start/Stop call,
            // otherwise a fast job could emit JobRemoved before we are watching.
            await EnsureSubscribedAsync(cancellationToken).ConfigureAwait(false);

            observer = await manager.WatchJobRemovedAsync(args =>
            {
                // Only our job matters; JobRemoved is global to all jobs on the instance.
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
                _ => throw new ArgumentOutOfRangeException(nameof(job), job, null),
            };

            // Publish only after the verb returned: D-Bus ordering guarantees the
            // JobRemoved signal arrives after this method reply.
            pendingJob.Set(jobPath);
            uint jobId = ParseJobId(jobPath);

            if (completion is null)
            {
                return new JobOutcome(jobId, jobPath, Result: null);
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            (uint Id, string Result) result = await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new JobOutcome(result.Id, jobPath, result.Result);
        }
        finally
        {
            observer?.Dispose();
        }
    }

    /// <summary>Full state of one unit. Tries GetUnit (already loaded) then falls back to LoadUnit.</summary>
    public async Task<UnitStatus> GetUnitStatusAsync(string unit, CancellationToken cancellationToken = default)
    {
        string name = UnitName.Normalize(unit);
        ObjectPath path = await GetUnitPathAsync(name).ConfigureAwait(false);

        // GetAll in one round trip instead of N single GetProperty calls.
        UnitProperties props = await GetService().CreateUnit(path).GetPropertiesAsync().ConfigureAwait(false);

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
            // Not loaded yet: ask systemd to load the unit file.
            return await manager.LoadUnitAsync(unitName).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Watch a unit's PropertiesChanged signal.
    /// The callback receives only the properties that changed (others are null);
    /// callers must treat null as "unchanged in this event".
    /// Returns a disposable that removes the observer.
    /// </summary>
    public async ValueTask<IDisposable> WatchUnitStateAsync(
        string unit,
        Action<UnitStateChange> onStateChanged,
        CancellationToken cancellationToken = default)
    {
        string name = UnitName.Normalize(unit);
        ArgumentNullException.ThrowIfNull(onStateChanged);

        await EnsureSubscribedAsync(cancellationToken).ConfigureAwait(false);
        ObjectPath path = await GetUnitPathAsync(name).ConfigureAwait(false);

        return await GetService().CreateUnit(path).WatchPropertiesChangedAsync(changed =>
        {
            // Never let an exception escape a signal handler: it can drop the connection.
            try
            {
                onStateChanged(new UnitStateChange(
                    LoadState: changed.LoadState,
                    ActiveState: changed.ActiveState,
                    SubState: changed.SubState,
                    UnitFileState: changed.UnitFileState,
                    Job: changed.Job));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handler failed for unit state watch on {Unit}", name);
            }
        }, emitOnCapturedContext: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Watch JobRemoved signals filtered to one unit.
    /// Returns a disposable that removes the observer.
    /// </summary>
    public async ValueTask<IDisposable> WatchJobRemovedAsync(
        string unit,
        Action<(uint JobId, string Unit, string Result)> onJobRemoved,
        CancellationToken cancellationToken = default)
    {
        string name = UnitName.Normalize(unit);
        ArgumentNullException.ThrowIfNull(onJobRemoved);

        await EnsureSubscribedAsync(cancellationToken).ConfigureAwait(false);

        return await GetManager().WatchJobRemovedAsync(args =>
        {
            try
            {
                if (args.Unit == name)
                {
                    onJobRemoved((args.Id, args.Unit, args.Result));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handler failed for job watch on {Unit}", name);
            }
        }, emitOnCapturedContext: false).ConfigureAwait(false);
    }

    /// <summary>Job object paths look like /org/freedesktop/systemd1/job/42 — extract the numeric id.</summary>
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

    /// <summary>
    /// Lock-guarded cell holding the job path we are waiting for. The cell is written
    /// after the Start/Stop reply and read from the JobRemoved signal handler, which may
    /// run on a different thread; the lock plus D-Bus message ordering close the race.
    /// </summary>
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
}
