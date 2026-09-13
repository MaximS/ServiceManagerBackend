# systemd over D-Bus: the `org.freedesktop.systemd1` API

> Part 2 of 3. Reference for the concrete D-Bus API of the systemd service
> manager (PID 1): object model, methods, properties, signals, states,
> security, and the job-completion dance. Primary source: the
> `org.freedesktop.systemd1(5)` man page; verified here against a live
> systemd 259 via `busctl introspect`.

## 1. Coordinates

| Item | Value |
|------|-------|
| Bus | **system bus** (addendum: the user instance lives on the **session bus**) |
| Service (well-known name) | `org.freedesktop.systemd1` |
| Owner | PID 1 (`systemd`) (user instance: the per-user systemd process) |
| Manager object path | `/org/freedesktop/systemd1` |
| Manager interface | `org.freedesktop.systemd1.Manager` |
| Unit object paths | `/org/freedesktop/systemd1/unit/<encoded-name>` |
| Unit interfaces | `org.freedesktop.systemd1.Unit` + type-specific (`...Service`, `...Socket`, `...Timer`, ...) |
| Job object paths | `/org/freedesktop/systemd1/job/<numeric-id>` |
| Job interface | `org.freedesktop.systemd1.Job` |

### Unit-name → object-path encoding

A unit name is encoded into its object path by replacing **every character
that is not a letter or digit** with `_` + two lowercase hex digits (the
character code):

| Unit name | Object path suffix |
|-----------|--------------------|
| `ssh.service` | `ssh_2eservice` (`.` → `_2e`) |
| `display-manager.target` | `display_2dmanager_2etarget` (`-` → `_2d`) |
| `getty@tty1.service` | `getty_40tty1_2eservice` (`@` → `_40`) |

You almost never compute this by hand: `Manager.GetUnit(name)` /
`LoadUnit(name)` return the path. Compute it only if you must construct
paths from names without a round trip (e.g. inside a signal handler that
already carries the name).

> **Addendum: the user instance (`systemctl --user`).** The per-user
> systemd instance is the *same service on a different bus*: same
> well-known name, same Manager object path, same unit/job path encoding,
> same methods, properties, signals, job modes, and job results — every
> section of this document applies unchanged. The deltas are:
>
> - **Bus**: session bus (`$XDG_RUNTIME_DIR/bus`) instead of the system
>   bus; the instance is started by systemd-logind at login, so it only
>   exists inside a login session (headless/cron contexts need
>   `XDG_RUNTIME_DIR` set and the instance running, e.g. via `loginctl
>   enable-linger <user>`).
> - **Scope**: units run in the user's session and stop when the last
>   session closes (linger keeps them alive).
> - **Unit files**: `~/.config/systemd/user/` for the user's own units, so
>   `ListUnitFiles`/`GetUnitFileState`/`EnableUnitFiles` operate on a
>   different directory tree and never touch system directories.
> - **Authorization**: the session owner manages their own units without
>   polkit authentication (the session bus is already owner-restricted),
>   so `InteractiveAuthorizationRequired` is not something you will meet
>   here.

## 2. The Manager object

The single entry point. Almost everything a control tool needs is a Manager
method. Live surface on systemd 259: the Manager interface itself has
**89 methods, 126 properties, 7 signals** (plus the standard
Peer/Introspectable/Properties interfaces on the same object). The XML in
`example/dbus-xml/` contains the full Manager interface as introspected.

### 2.1 Unit operations — `(name, mode) → job path`

The core verb family. Each takes the unit name and a **mode** string and
returns the **object path of the newly enqueued job** (the operation is
*asynchronous* — see §5):

```
StartUnit(name, mode) → o
StopUnit(name, mode)  → o
RestartUnit(name, mode) → o
TryRestartUnit(name, mode) → o
ReloadUnit(name, mode) → o
ReloadOrRestartUnit(name, mode) → o
ReloadOrTryRestartUnit(name, mode) → o
```

**Job mode strings** (the second argument):

| Mode | Behavior | Use it when |
|------|----------|-------------|
| `fail` | start/stop the unit + dependencies; **fail** if it would conflict with an already-queued job | the safe default for most scripting |
| `replace` | same, but **replaces** conflicting queued jobs | `systemctl`-like "just do it" semantics (default in the example) |
| `isolate` | start the unit and **stop everything not it depends on** | switching targets (`isolated multi-user.target`); invalid for `StopUnit` |
| `ignore-dependencies` | act on the unit, ignore *all* dependencies | escape hatch, discouraged |
| `ignore-requirements` | act, ignoring only *requirement*-type dependencies | escape hatch, discouraged |

The same verbs exist **on the unit objects themselves** (`Unit.Start(mode)`,
`Unit.Stop(mode)`, ...) without the name argument — a round-trip
optimization: calling `Manager.StartUnit("ssh.service", ...)` is one call;
calling `Unit.Start(...)` requires `GetUnit` first *unless* you already hold
the unit's path (e.g. from a signal).

Other useful Manager methods:

```
GetUnit(name) → o                     path of a loaded unit (fails if not loaded)
LoadUnit(name) → o                    like GetUnit, but loads from disk first
GetUnitByPID(pid) → o                 which unit owns this process
GetJob(id) → o                        job path from numeric id
ListJobs() → a(usssoo)                (id, unit, type, state, job path, unit path)
CancelJob(id)                         abort a queued job (no effect once running)
ClearJobs()                           flush all queued (not yet running) jobs
ListUnits() → a(ssssssouso)           all loaded units (see §2.4)
ListUnitFiles() → a(ss)               (unit file, enablement state) on disk
GetUnitFileState(name) → s            enabled/disabled/static/masked/...
EnableUnitFiles(as names, b runtime, b force) → b a(sss)
DisableUnitFiles(as names, b runtime) → a(sss)
ResetFailedUnit(name) / ResetFailed()
KillUnit(name, whom, signal)          whom: main|control|cgroup|all (suffix "-fail" to require a match)
Subscribe() / Unsubscribe()           see §6 — CRITICAL for signals
Reload() / Reexecute()                manager reload (needs reload-daemon privilege)
SetEnvironment(as) / UnsetEnvironment(as)
StartTransientUnit(name, mode, a(sv) properties, as aux) → o
```

`EnableUnitFiles`/`DisableUnitFiles` return `(carriesInstallInfo, changes)`
where each change is `(kind, symlink, destination)` with `kind` one of
`symlink`/`unlink` — i.e. the actual link operations systemd performed.

### 2.2 The `ListUnits()` struct

`a(ssssssouso)` — array of structs, fields in order:

| # | Type | Field |
|---|------|-------|
| 1 | `s` | primary unit name |
| 2 | `s` | description |
| 3 | `s` | load state (`loaded`/`not-found`/`error`/`masked`) |
| 4 | `s` | active state (`active`/`inactive`/`failed`/`activating`/`deactivating`/...) |
| 5 | `s` | sub state (type-specific, e.g. `running`, `dead`) |
| 6 | `s` | following (empty unless the unit follows another) |
| 7 | `o` | unit object path |
| 8 | `u` | queued job id (`0` = none) |
| 9 | `s` | queued job type (`""` = none) |
| 10 | `o` | queued job path |

### 2.3 Manager signals

```
UnitNew(id s, unit o)          unit loaded into memory
UnitRemoved(id s, unit o)      unit unloaded from memory
JobNew(id u, unit o, unit-name s)
JobRemoved(id u, unit o, unit-name s, result s)   ← the one you await
StartupFinished(t ×6)          boot timing, informational
UnitFilesChanged()             enablement/masking changed on disk
Reloading(b)                   True before, False after a daemon reload
```

### 2.4 Manager properties (selected)

`Version` (informational — **do not parse**), `SystemState`
(`initializing|starting|running|degraded|maintenance|stopping`), `NUnits`/
`NFailedJobs` counters, `Environment` (`as`), `DefaultTarget`,
`UnitPath` (`as`, the unit search path), boot timestamps.

## 3. The Unit object

Each loaded unit is an object at its encoded path, implementing
`org.freedesktop.systemd1.Unit` plus a type-specific interface (e.g.
`org.freedesktop.systemd1.Service` adds `MainPID`, `Result`, `NRestarts`,
`ExecMainStatus`, ...).

### 3.1 State properties (the ones a controller displays)

| Property | Type | Values / notes |
|----------|------|----------------|
| `Id` | `s` | primary unit name (const) |
| `Names` | `as` | all names including aliases |
| `Description` | `s` | human description |
| `LoadState` | `s` | `loaded` / `not-found` / `error` / `masked` |
| `ActiveState` | `s` | `active` / `inactive` / `failed` / `activating` / `deactivating` / `maintenance` / `reloading` / `refreshing` |
| `SubState` | `s` | fine-grained, type-specific (e.g. `running`, `dead`, `exited`, `auto-restart`) |
| `UnitFileState` | `s` | `enabled`/`enabled-runtime`/`linked`/`static`/`masked`/`disabled`/... |
| `FragmentPath` | `s` | unit file path (empty if none) |
| `NeedDaemonReload` | `b` | config on disk changed since load |
| `CanStart`/`CanStop`/`CanReload`/`CanIsolate` | `b` | capability flags (not privileges!) |
| `Job` | `(uo)` | `(job id, job path)` of the queued/running job; id `0` = none |
| `StateChangeTimestamp` | `t` | µs since epoch (CLOCK_REALTIME) of last state change |
| `ActiveEnterTimestamp` | `t` | µs, last transition into `active` |

`LoadState` and `ActiveState` are **orthogonal**: a unit can be `active`
with a `masked`/`error` load state (it was started before its file changed).

Type-specific examples (`org.freedesktop.systemd1.Service`):
`MainPID` (`u`), `Result` (`s`: `success`/`exit-code`/`signal`/`timeout`/
`start-limit-hit`/...), `ExecMainStatus` (`i`), `NRestarts` (`u`),
`ActiveEnterTimestampMonotonic`, ...

### 3.2 Unit methods

`Start(mode)`, `Stop(mode)`, `Restart(mode)`, `TryRestart(mode)`,
`Reload(mode)`, `ReloadOrRestart(mode)`, `ReloadOrTryRestart(mode)`,
`Kill(whom, signal)`, `QueueSignal(whom, signal, value)`, `ResetFailed()`,
`SetProperties(b runtime, a(sv) props)`, `Clean(as mask)`, `Freeze()`,
`Thaw()`, `Ref()`/`Unref()`. Same semantics as the Manager verbs; they just
drop the name argument.

## 4. The Job object

Operations on units are **jobs**: entries in systemd's transaction queue.
`StartUnit` returns a job path; the job then runs (waiting/running) and is
finally removed. Job object (path `/org/freedesktop/systemd1/job/<id>`):

```
interface org.freedesktop.systemd1.Job {
  method Cancel();
  method GetAfter() → a(usssoo);
  method GetBefore() → a(usssoo);
  property u Id;             const; unique per manager lifetime
  property (so) Unit;        (unit name, unit path)
  property s JobType;        start|stop|reload|restart|try-restart|reload-or-restart|verify-active
  property s State;          waiting | running
  property a(ss) ActivationDetails;
};
```

### Job results (the `result` field of `JobRemoved`)

| Result | Meaning |
|--------|---------|
| `done` | **success** |
| `canceled` | canceled via `CancelJob` before/while finishing |
| `timeout` | job timeout reached (`JobTimeoutUSec`) |
| `failed` | the operation failed (service exited non-zero, ...) |
| `dependency` | a dependency of this job failed, so it was removed |
| `skipped` | the job did not apply to the unit's current state |

A job "finished successfully" **iff `result == "done"`**. Map every other
value to failure in your tooling.

## 5. The job-completion dance (the most important pattern)

`StartUnit` returns as soon as the job is **enqueued**, not when the unit
has started. To know the outcome you must observe `JobRemoved` and match it
to *your* job. The race-free sequence (mandated by the man page):

```
1. Subscribe()                          # signals are gated — see §6
2. install a JobRemoved signal handler  # match rule must exist BEFORE the call
3. jobPath = StartUnit(name, mode)      # method return arrives first...
4. remember jobPath                     # ...any JobRemoved for it arrives after
5. in the handler: if (args.job == jobPath) → resolve with args.result
```

Why it is race-free: D-Bus delivers messages on one connection in order, so
the method return for step 3 is always processed before the `JobRemoved`
signal for that job. The only ordering you must guarantee is **handler
installed before the method call**.

Do **not** poll `ListJobs` or read `Unit.Job` in a loop — the signal
approach is exact, allocation-free, and what `systemctl --wait` does
internally.

## 6. The `Subscribe()` gotcha

systemd suppresses **most** of its signals (including
`PropertiesChanged` on unit objects and `JobRemoved`) unless at least one
client has called `Manager.Subscribe()` on its connection. systemd tracks
this per connection and stops emitting once the last subscriber disconnects
or calls `Unsubscribe()`.

Consequences:

- A client that only calls `ListUnits` never needs `Subscribe()`.
- Any client that **watches** `JobRemoved`, `UnitNew`, or unit
  `PropertiesChanged` **must** `Subscribe()` first, or it will silently
  receive nothing. (Your `busctl monitor` works because busctl subscribes.)
- It is a per-connection flag; calling it multiple times is harmless, but
  keep one explicit `Unsubscribe()` on the way out for cleanliness.

## 7. Security / polkit

- **Read access**: everyone (any method that only reads, any property, any
  signal).
- **Unit state changes** (`StartUnit`, `StopUnit`, `RestartUnit`,
  `KillUnit`, `SetProperties`, ...): polkit action
  `org.freedesktop.systemd1.manage-units`.
- **Unit-file enablement** (`EnableUnitFiles`, `DisableUnitFiles`,
  `MaskUnitFiles`, `LinkUnitFiles`, `PresetUnitFiles`, ...):
  `org.freedesktop.systemd1.manage-unit-files`.
- `SetEnvironment`/`UnsetEnvironment`: `org.freedesktop.systemd1.set-environment`.
- `Reload`/`Reexecute`: `org.freedesktop.systemd1.reload-daemon`.
- `Reboot`/`PowerOff`/`Halt`/`KExec`: `org.freedesktop.systemd1.{shutdown,poweroff,reboot,halt}` —
  and you should use `org.freedesktop.login1` instead of these.

What this means in practice:

- root: everything works.
- unprivileged user, no polkit rule: mutating calls fail with
  `AccessDenied` — or, if the caller did not enable *interactive
  authorization*, immediately with
  `org.freedesktop.DBus.Error.InteractiveAuthorizationRequired`.
- `busctl`/`dbus-send` **do** enable interactive authorization, so an
  unprivileged `busctl call ... StartUnit` will *hang for ~25 s* waiting for
  a polkit agent prompt before failing in a headless environment. Tmds.DBus
  does not set that flag, so you fail fast — handle the error name and tell
  the user to `sudo` (or install a polkit rule, e.g. via
  `/etc/polkit-1/rules.d/`).

## 8. D-Bus error names systemd emits

| Error name | When |
|------------|------|
| `org.freedesktop.systemd1.UnitNotFound` | `GetUnit`/`LoadUnit` for an unknown name |
| `org.freedesktop.systemd1.UnitExists` | e.g. creating a transient unit that already exists |
| `org.freedesktop.systemd1.UnitMasked` | operating on a masked unit |
| `org.freedesktop.systemd1.UnitNotActive` | e.g. `StopUnit` on an inactive unit (mode-dependent) |
| `org.freedesktop.systemd1.NoSuchJob` | `GetJob`/`CancelJob` with an unknown id |
| `org.freedesktop.systemd1.JobCancelled` | job was canceled |
| `org.freedesktop.systemd1.AccessDenied` | policy denied the operation |
| `org.freedesktop.DBus.Error.AccessDenied` / `...InteractiveAuthorizationRequired` / `org.freedesktop.PolicyKit1.Error.Failed` | polkit layers |
| `org.freedesktop.DBus.Error.NoReply` | the peer timed out (e.g. systemd blocked on an auth prompt) |

The error **name** is the stable, programmatic part — branch on it. The
message text is for humans and changes between versions.

## 9. Cheat sheet: shell ↔ D-Bus ↔ C# (Tmds.DBus)

| Task | busctl | C# (generated proxy) |
|------|--------|----------------------|
| List units | `busctl call ... Manager ListUnits` | `await manager.ListUnitsAsync()` |
| Unit state | `busctl get-property ... Unit ActiveState` | `await unit.GetActiveStateAsync()` / `GetPropertiesAsync()` |
| Start unit | `busctl call ... Manager StartUnit ss NAME replace` | `ObjectPath job = await manager.StartUnitAsync(name, "replace")` |
| Wait for job | `busctl monitor` (watch `JobRemoved`) | `Subscribe()` + `WatchJobRemovedAsync` + match on job path (§5) |
| Enable file | `busctl call ... Manager EnableUnitFiles asbb [...] false false` | `await manager.EnableUnitFilesAsync(files, runtime, force)` |
| Watch state | `busctl monitor ...` | `await unit.WatchPropertiesChangedAsync(...)` |
| Introspect | `busctl --xml introspect ...` | (build-time: feed XML to the source generator) |

## 10. Versioning notes

- Interfaces follow the D-Bus interface versioning guidelines: members are
  only added, never removed or re-typed. New job-type/result strings may
  appear — treat unknown string values as "pass through / display as-is",
  never as a parse error.
- `Manager.Version` is explicitly *not* a stable API; never parse it.
- Timestamps are **microseconds**; realtime ones are µs since the Unix
  epoch, `*Monotonic` ones are µs on CLOCK_MONOTONIC (do not mix them).
