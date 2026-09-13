---
name: systemd-dbus-dotnet
description: Scaffold a .NET project that controls systemd services (units) over D-Bus using Tmds.DBus.Protocol and the Tmds.DBus.Generator source generator. Use when asked to create or extend a systemd service controller, unit manager, or any .NET client of the org.freedesktop.systemd1 D-Bus API.
---

# Skill: systemd D-Bus controller in .NET

Build a C# project (Tmds.DBus.Protocol + Tmds.DBus.Generator) that talks to
PID 1 over the system D-Bus bus. A complete, verified reference
implementation exists in this repository at
`example/SystemdServiceController/` — **prefer copying/adapting it over
writing from scratch.** Deep background: `docs/01..03*.md`.

## 0. Hard facts (do not rediscover)

- Bus: **system bus** (`DBusAddress.System`). Service name:
  `org.freedesktop.systemd1`. Manager object: `/org/freedesktop/systemd1`,
  interface `org.freedesktop.systemd1.Manager`.
- Unit objects: `/org/freedesktop/systemd1/unit/<encoded>` where every
  non-alphanumeric char becomes `_`+2 lowercase hex (`.`→`_2e`, `-`→`_2d`,
  `@`→`_40`). **Never compute paths by hand when a call can return them** —
  use `GetUnit(name)`/`LoadUnit(name)`.
- Unit names sent to the API must be **fully suffixed**: the manager does
  NOT append `.service`. Normalize client-side: no `.` in name → append
  `.service`.
- All unit operations are **asynchronous jobs**: `StartUnit(name, mode)`
  returns a **job object path** immediately. Completion arrives later as the
  `JobRemoved(id, unitPath, unitName, result)` signal.
- **Signals are suppressed unless `Manager.Subscribe()` was called** on the
  connection (per-connection flag). Call it before installing any signal
  watch (JobRemoved, UnitNew, unit PropertiesChanged).
- Job result strings: `done` = success; `failed`, `canceled`, `timeout`,
  `dependency`, `skipped` = not success. Branch on this.
- Job mode strings for the 2nd method arg: `fail`, `replace` (use this
  default), `isolate`, `ignore-dependencies`, `ignore-requirements`.
- Security: reads = everyone; mutations need polkit
  `org.freedesktop.systemd1.manage-units` (unit ops) or
  `org.freedesktop.systemd1.manage-unit-files` (enable/disable). Unprivileged
  mutation via Tmds.DBus fails **fast** with
  `org.freedesktop.DBus.Error.InteractiveAuthorizationRequired` (the lib does
  not request interactive auth). Branch on `DBusErrorReplyException.ErrorName`.
- Error names to map: `org.freedesktop.systemd1.UnitNotFound`,
  `.UnitMasked`, `.UnitNotActive`, `org.freedesktop.DBus.Error.AccessDenied`,
  `org.freedesktop.DBus.Error.InteractiveAuthorizationRequired`,
  `org.freedesktop.DBus.Error.NoReply`.
- Generated proxy methods accept **no CancellationToken**; wrap with
  `Task.WaitAsync(timeout, ct)` where a bound is needed.
- **User-space systemd (addendum, non-fundamental):** the per-user
  instance (`systemctl --user`) is the *same* service name, object paths,
  and API on the **session bus** (`DBusAddress.Session`). Supporting a
  `--user` flag means changing only the bus address — nothing else in this
  skill differs. Preconditions: `$XDG_RUNTIME_DIR` set and the user
  instance running (login session, or `loginctl enable-linger <user>`).
  Unit files live in `~/.config/systemd/user/`; the session owner manages
  own units without polkit auth, so `InteractiveAuthorizationRequired`
  will not occur there.

## 1. Verify environment (before writing code)

```bash
dotnet --version                                   # need SDK >= 8
[ -S /run/dbus/system_bus_socket ] && echo bus-ok  # system bus present?
busctl --no-pager list | grep org.freedesktop.systemd1   # systemd on the bus?
[ -S "$XDG_RUNTIME_DIR/bus" ] && echo user-bus-ok # session bus present? (user instance)
```

If there is no system bus (common in plain containers), the project still
must build; just note that runtime verification is limited and skip §5.3.

## 2. Scaffold

```bash
dotnet new console -o <Name>            # OutputType Exe, net8.0+
cd <Name>
dotnet add package Tmds.DBus.Protocol --version 0.95.1
dotnet add package Tmds.DBus.Generator --version 0.95.1
mkdir dbus-xml
```

`<Name>.csproj` — the exact shape that works:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>  <!-- or newer installed TFM -->
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Tmds.DBus.Protocol" Version="0.95.1" />
    <PackageReference Include="Tmds.DBus.Generator" Version="0.95.1" />
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="dbus-xml/org.freedesktop.systemd1.Manager.xml" Namespace="Systemd.DBus" DBusGeneratorMode="Proxy" Visibility="public" />
    <AdditionalFiles Include="dbus-xml/org.freedesktop.systemd1.Unit.xml" Namespace="Systemd.DBus" DBusGeneratorMode="Proxy" Visibility="public" />
    <AdditionalFiles Include="dbus-xml/org.freedesktop.systemd1.Job.xml" Namespace="Systemd.DBus" DBusGeneratorMode="Proxy" Visibility="public" />
  </ItemGroup>
</Project>
```

NEVER set `CompilerGeneratedFilesOutputPath` to the project root (or any dir
the SDK compiles): the emitted .cs gets compiled twice → CS0101 storm.

## 3. Obtain the interface XML

Option A (fastest): copy `example/SystemdServiceController/dbus-xml/` from
this repo.

Option B (fresh, version-accurate): introspect the live system, keep only
the systemd interfaces:

```bash
busctl --no-pager --xml introspect org.freedesktop.systemd1 /org/freedesktop/systemd1 > /tmp/m.xml
busctl --no-pager --xml introspect org.freedesktop.systemd1 <any-unit-path> > /tmp/u.xml
```

Then extract each `<interface name="org.freedesktop.systemd1.X">…</interface>`
into its own file, wrapped in:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN"
"https://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
<node> …one interface… </node>
```

Drop `org.freedesktop.DBus.Peer/Introspectable/Properties` (the generator
provides property plumbing itself). `org.freedesktop.systemd1.Job` is
transient (no job object exists when no job is queued); take its 5 members
from `org.freedesktop.systemd1(5)` man page: methods `Cancel`, `GetAfter`,
`GetBefore`; properties `u Id`, `(so) Unit`, `s JobType`, `s State`,
`a(ss) ActivationDetails`.

## 4. Implement `SystemdClient` (connection + typed operations)

Wrap everything; the CLI/consumer never touches `DBusConnection` directly.
House style: file-scoped namespaces, `#nullable`, every public async method
ends with `CancellationToken cancellationToken = default`, all awaits use
`.ConfigureAwait(false)`, `ValueTask` for cheap operations, `IAsyncDisposable`
for the client, `ArgumentException.ThrowIfNull` /
`ArgumentNullException.ThrowIfNull` on inputs.

Required structure (see `example/…/SystemdClient.cs` for the full code):

1. Constructor: resolve `DBusAddress.System` (throw `InvalidOperationException`
   with a clear message if null); `new DBusConnectionOptions(addr)` with
   `OnException` logging to stderr; `new DBusConnection(options)`.
2. `ConnectAsync`: `await connection.ConnectAsync()` →
   `await connection.WatchNameOwnerAsync("org.freedesktop.systemd1")` →
   `await watcher.WaitForOwnerAsync(ct)` →
   `new DBusService(connection, NameOwnerWatcher.GetOwnerBusName(ownerIdentifier))`.
   NOTE: `DBusService` is a **struct**; a `DBusService?` field cannot call
   the `CreateXxx` extension methods — dereference through a non-nullable
   local.
3. `EnsureSubscribedAsync()`: idempotent `await manager.SubscribeAsync()`
   (guard with a lock + bool). Must run before ANY signal watch.
4. `EnqueueUnitJobAsync(unit, job, waitForCompletion, ct)` — the
   race-free job pattern, in this exact order:
   a. if waiting: `EnsureSubscribedAsync()`;
   b. `IDisposable observer = await manager.WatchJobRemovedAsync(args => { if (pending.Is(args.Job)) tcs.TrySetResult((args.Id, args.Result)); }, emitOnCapturedContext: false);`
      (`pending` = a lock-guarded cell holding `ObjectPath?`; the method
      return continuation and the signal handler may run on different
      threads — D-Bus ordering guarantees the signal arrives after the
      return, the cell publishes the path safely);
   c. call the verb: `manager.StartUnitAsync(name, "replace")` /
      `StopUnitAsync` / `RestartUnitAsync` / `TryRestartUnitAsync` /
      `ReloadUnitAsync` / `ReloadOrRestartUnitAsync` → `ObjectPath jobPath`;
   d. `pending.Set(jobPath)`;
   e. if waiting: `await tcs.Task.WaitAsync(linkedCt with CancelAfter(10 min))`;
      map result: `done` → success, anything else → failure with the result
      string. `finally { observer?.Dispose(); }`
5. `GetUnitStatusAsync(unit, ct)`: `GetUnitAsync(name)` with
   `LoadUnitAsync` fallback (catch `DBusErrorReplyException`); then
   `GetService().CreateUnit(path).GetPropertiesAsync()` (one round trip);
   if `LoadState == "not-found"` surface that distinctly.
6. `WatchUnitAsync(unit, handler, ct)`: `EnsureSubscribedAsync()` +
   `unitProxy.WatchPropertiesChangedAsync(changed => …)` (+ optional
   `manager.WatchJobRemovedAsync` filtered by `args.Unit == name`); return a
   composite `IDisposable` that disposes all observers. In the handler,
   changed properties are nullable (`IChangedUnitProperties`): `HasXxxChanged`
   true + `Xxx == null` means *invalidated* → re-fetch via `GetXxxAsync()`.
7. `DisposeAsync`: best-effort `UnsubscribeAsync()`, dispose
   `NameOwnerWatcher`, dispose `DBusConnection`.

Useful generated members (names verified against the generator output):
`ListUnitsAsync() → (s,s,s,s,s,s,o,u,s,o)[]` (use `.Length`; map to records
immediately), `ListUnitFilesAsync() → (s,s)[]`,
`ListJobsAsync() → (u,s,s,s,o,o)[]`, `GetUnitFileStateAsync(name) → string`,
`EnableUnitFilesAsync(string[] files, bool runtime, bool force) →
(bool, (s,s,s)[])`, `DisableUnitFilesAsync(string[], bool) → (s,s,s)[]`,
`ResetFailedUnitAsync(name)` / `ResetFailedAsync()`, `GetVersionAsync()`,
`Unit.StartAsync(mode)/StopAsync/RestartAsync/...` (same verbs without
name).

## 5. Verify

5.1 Build: `dotnet build` → 0 warnings, 0 errors. If CS0101 "already
contains definition": generated files are being double-compiled (§2 note) or
two XMLs define the same interface.

5.2 Read-only smoke test (works unprivileged):

```bash
dotnet run -- version                      # → "systemd <ver>"
dotnet run -- list <some-suffix>           # table incl. a row
dotnet run -- status <unit>.service        # State/Load/File/Fragment
dotnet run -- jobs                         # "No queued jobs." or a table
```

Cross-check any result with
`busctl --no-pager get-property org.freedesktop.systemd1 <unit-path> org.freedesktop.systemd1.Unit ActiveState`.

5.3 Privileged path (only if root/sudo available):
`sudo dotnet run -- restart <some-safe-unit>` must end with
`… finished: done` and exit code 0. Unprivileged mutation must fail fast
with the friendly "Not authorized … Re-run with sudo" message and exit 1 —
that is correct behavior, not a bug.

5.4 Watch path: `timeout -s INT 5 dotnet run -- watch <unit>.service`
prints the initial state then "Stopped watching." (exit 124 comes from
`timeout`, not the app).

## 6. Pitfalls (encountered, in order of cost)

1. Forgetting `Subscribe()` → zero signals, silent.
2. Installing the `JobRemoved` watcher after the `StartUnit` call → race.
3. `CompilerGeneratedFilesOutputPath` inside the project dir → CS0101 storm.
4. `DBusService?` (Nullable struct) → CS1929 on `CreateManager`/`CreateUnit`.
5. Treating `ListUnitsAsync()` result as a `List<T>` (it's a tuple array:
   `.Length`, deconstruct).
6. Sending unsuffixed unit names → `UnitNotFound`; normalize `.service`.
7. Letting an exception escape a signal handler → the connection can be
   disconnected. Catch inside handlers.
8. Expecting interactive polkit auth to prompt — it doesn't; map
   `InteractiveAuthorizationRequired`.
9. Parsing `Manager.Version` — not a stable API.
10. Mixing realtime (`t` µs since epoch) and `*Monotonic` timestamps.

## 7. Definition of done

- Project builds clean; read-only commands verified against the live bus
  (or build-only verified when no bus exists).
- Job verbs use the §4.4 pattern; `--no-wait` variant exists.
- Error output maps `ErrorName` → human message + remediation hint;
  meaningful exit codes (0 ok, 1 error, 4 unit-not-found).
- No magic strings: bus name, object paths, mode strings, result strings are
  named constants.
