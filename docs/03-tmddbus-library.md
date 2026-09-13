# Tmds.DBus: controlling systemd from .NET

> Part 3 of 3. How the [Tmds.DBus](https://tmds.github.io/Tmds.DBus/)
> library maps D-Bus (Part 1) onto C#, how the source generator works, and a
> walkthrough of the runnable example in `example/SystemdServiceController`.

## 1. The packages

| Package | Role | Status |
|---------|------|--------|
| **`Tmds.DBus.Protocol`** | The modern D-Bus protocol implementation (netstandard2.0/2.1, net6+; NativeAOT/trimming-friendly) | **use this** |
| **`Tmds.DBus.Generator`** | Roslyn **source generator** that turns D-Bus interface XML into C# proxy/handler types at build time | recommended codegen path |
| `Tmds.DBus` (no suffix) | Older dbus-sharp-based library | maintenance mode, don't start new projects on it |
| `Tmds.DBus.Tool` | `dotnet dbus` global tool: list services/objects, `codegen` (static file generation), `monitor` | dev tooling |

Latest stable at the time of writing: **0.95.1**.

Two code-generation strategies exist:

1. **Source generator** (`Tmds.DBus.Generator`, build-time, from XML) —
   what the official docs recommend and what the example project uses.
2. **Static codegen** (`dotnet dbus codegen --protocol-api ...`, produces a
   `.cs` file you commit) — that is how the `Systemd.DBus.cs` at the repo
   root was produced. See §8 for the differences.

## 2. Connecting

```csharp
using Tmds.DBus.Protocol;

string? address = DBusAddress.System;      // null if no system bus exists
// or: new DBusConnectionOptions(address) { AutoConnect = true,
//                                           OnException = ctx => ... };
using var connection = new DBusConnection(address!);
await connection.ConnectAsync();
```

Key points:

- `DBusAddress.System` / `DBusAddress.Session` resolve the standard socket
  paths for the current host; both are `null` when that bus is absent.
  (System bus: `unix:path=/run/dbus/system_bus_socket`.)
- **User instance** (`systemctl --user`): use `DBusAddress.Session` and
  that is the *only* change — same service name, same object paths, same
  generated proxies (Part 2, §1 addendum). The session bus must exist, i.e.
  `$XDG_RUNTIME_DIR` is set and the per-user instance is running.
- `DBusConnectionOptions` knobs:
  - `AutoConnect` — connect lazily on first use (consumer-side only).
  - `OnException` — callback for connection-level errors (log them).
- The static `DBusConnection.System` / `.Session` give a **shared**
  auto-connect instance (handy for quick tools; for a long-lived app create
  your own connection and own its lifecycle).
- `await connection.DisconnectedAsync()` → `Exception?` completes when the
  connection drops (`null` on clean dispose). Useful for a "reconnect" loop
  on the service side.
- Closing: `connection.Dispose()`.

### Exceptions

| Type | Meaning |
|------|---------|
| `DBusConnectFailedException` | could not connect to the bus |
| `DBusConnectionClosedException` | connection dropped mid-operation (`InnerException` = reason) |
| `DBusErrorReplyException` | the *service* returned a D-Bus error reply. **`ErrorName`** (stable, branch on it) + **`ErrorMessage`** (human text) |
| `DBusOwnerChangedException` | the pinned owner of a well-known name changed (only when using owner-pinned destinations) |
| `DBusUnexpectedValueException` | reply body didn't match expectations |

All derive from `DBusConnectionException` / `DBusMessageException`
respectively; catch `DBusMessageException` broadly, then refine on
`ErrorName` (see the example's `Cli.FailBusError`).

## 3. From XML to C#: the source generator

### 3.1 Get the interface XML

Sources, in order of preference:

1. **Introspect the live service**:
   `busctl --no-pager --xml introspect <name> <path>` — always matches the
   version running on the machine (used to build the example's
   `dbus-xml/` files).
2. Spec repos / vendor files (e.g. MPRIS on GitLab,
   `/usr/share/dbus-1/interfaces/`).
3. `dotnet dbus list objects --bus system` to discover paths first.

Keep **one `<interface>` per file**, drop the standard
`org.freedesktop.DBus.*` interfaces (the generator provides the property
plumbing itself):

```
dbus-xml/org.freedesktop.systemd1.Manager.xml   ← from introspecting /org/freedesktop/systemd1
dbus-xml/org.freedesktop.systemd1.Unit.xml      ← from introspecting any unit object
dbus-xml/org.freedesktop.systemd1.Job.xml       ← tiny; from the man page (jobs are transient)
```

### 3.2 Configure the project

```xml
<ItemGroup>
  <PackageReference Include="Tmds.DBus.Protocol" Version="0.95.1" />
  <PackageReference Include="Tmds.DBus.Generator" Version="0.95.1" />
</ItemGroup>
<ItemGroup>
  <AdditionalFiles Include="dbus-xml/org.freedesktop.systemd1.Manager.xml"
                   Namespace="Systemd.DBus" DBusGeneratorMode="Proxy" Visibility="public" />
  <!-- ...one entry per interface XML... -->
</ItemGroup>
```

- `Namespace` — where the generated types live (per-file).
- `DBusGeneratorMode` — `Proxy` (client) or `Handler` (server).
- `Visibility` — default `internal`; set `public` for a library.
- To **read** the generated source after a build, add:

  ```xml
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath>
  ```

  ⚠️ Do **not** point this at the project root — the SDK compiles every
  `*.cs` under the project directory, so root-level emission double-defines
  every type (CS0101 storm). `obj/` is excluded from compilation.

### 3.3 What the generator emits (Proxy mode)

For each `<interface name="...Foo">` you get, in your namespace:

| Generated | For |
|-----------|-----|
| `Foo : DBusObject` | one class per interface |
| `Foo.XxxAsync(args...) : Task<...>` | each `<method>` (C#-ified name + `Async`) |
| `Foo.GetXxxAsync() / SetXxxAsync(v)` | each `<property>` (get/set by access) |
| `Foo.GetPropertiesAsync() : Task<FooProperties>` | all readable properties in one round trip |
| `Foo.GetNullablePropertiesAsync() : Task<INullableFooProperties>` | same, but missing → `null` instead of throw |
| `Foo.WatchPropertiesChangedAsync(Action<IChangedFooProperties>) : ValueTask<IDisposable>` | the standard `PropertiesChanged` signal, typed |
| `Foo.WatchSignalAsync(...)` — actually `Foo.Watch<Signal>Async(...)` per signal | each `<signal>`; simple `Action<args>` and advanced `Action<Notification<args>>` overloads |
| `ObjectFactory.CreateFoo(this DBusService, ObjectPath)` | extension to instantiate the proxy on a service |
| `FooProperties`, `INullableFooProperties`, `IChangedFooProperties`, `FooProperty` enum | property plumbing |

Type mapping: D-Bus structs → **C# tuples** (positional, `Item1`/`Item2` or
names you can deconstruct), `o` → `ObjectPath`, `ay` → `byte[]`, `h` →
`SafeHandle`, `v` → `VariantValue`.

Method names are the D-Bus member verbatim (PascalCase already), e.g.
`StartUnit`, `GetUnit`, `ListUnits`. Note the generated call methods take
**no `CancellationToken`** — cancellation of an in-flight D-Bus call is not
part of the API; use timeouts at a higher level if needed.

## 4. The client flow (what the MPRIS example does, systemd-ed)

The official docs build an MPRIS media-player remote. The systemd equivalent
— same shape, different coordinates:

```csharp
// 1. Connect to the SYSTEM bus (systemd lives there).
using var connection = new DBusConnection(DBusAddress.System!);
await connection.ConnectAsync();

// 2. Pin to the current owner of the well-known name.
var watcher = await connection.WatchNameOwnerAsync("org.freedesktop.systemd1");
string ownerIdentifier = await watcher.WaitForOwnerAsync();   // "org.freedesktop.systemd1/:1.1"
string owner = NameOwnerWatcher.GetOwnerBusName(ownerIdentifier); // ":1.1"

// 3. Build proxies. DBusService is a STRUCT: (connection, destination).
var systemd = new DBusService(connection, owner);
var manager = systemd.CreateManager("/org/freedesktop/systemd1");
var unit    = systemd.CreateUnit("/org/freedesktop/systemd1/unit/ssh_2eservice");

// 4. Call methods (typed, awaitable).
ObjectPath job = await manager.StartUnitAsync("ssh.service", "replace");
string activeState = await unit.GetActiveStateAsync();

// 5. Read a property bag in one round trip.
UnitProperties props = await unit.GetPropertiesAsync();

// 6. Watch signals; the returned IDisposable stops the watch.
using var jobWatch = await manager.WatchJobRemovedAsync((uint id, ObjectPath jobPath, string unitName, string result) =>
    Console.WriteLine($"{unitName}: {result}"));
using var stateWatch = await unit.WatchPropertiesChangedAsync(changed =>
{
    if (changed.HasActiveStateChanged)
        Console.WriteLine($"active: {changed.ActiveState}");   // string? — may be null when invalidated
});
```

Differences vs the media-player example, and why:

- **System bus**, not session: `DBusAddress.System`.
- **No service discovery**: MPRIS players are discovered by listing names
  with a prefix (`connection.ListServicesAsync()`); systemd is a fixed,
  always-present name — you go straight to it.
- **`Subscribe()`**: MPRIS doesn't need it; systemd **does** (Part 2, §6) —
  the example calls it lazily before any signal watching
  (`SystemdClient.EnsureSubscribedAsync`).
- **Owner pinning**: the MPRIS example re-targets `firstPlayer` to the owner
  unique name. For systemd the name is stable (PID 1), so the example keeps
  the well-known name and only *records* the owner; either works. If you do
  pin, remember `NameOwnerWatcher.GetOwnerChangedCancellationToken(owner)`
  gives you a token that fires when the owner flips (e.g. `daemon-reexec`).

### `NameOwnerWatcher` in one breath

Well-known names can change owners (service restart/replacement). The
watcher: `WaitForOwnerAsync(ct) → string` (an **owner identifier**
`"name/:1.42"`, usable directly as a message destination),
`GetCurrentOwner()`, `GetOwnerBusName(identifier) → ":1.42"`,
`GetOwnerChangedCancellationToken(owner)`. `IDisposable`.

## 5. Signals in depth

Each D-Bus signal `Foo(args)` becomes **four** `WatchFooAsync` overloads:

```csharp
// (a) simple, sync: handler gets the args tuple directly
ValueTask<IDisposable> WatchFooAsync(Action<args> handler, bool emitOnCapturedContext = true)
// (b) simple, async: handler returns ValueTask
ValueTask<IDisposable> WatchFooAsync(Func<args, ValueTask> handler, bool emitOnCapturedContext = true)
// (c) advanced, sync: handler gets Notification<args> (see flags)
ValueTask<IDisposable> WatchFooAsync(Action<Notification<args>> handler, ObserverFlags flags, bool emitOnCapturedContext = true, object? state = null)
// (d) advanced, async: same with Func
```

- The returned **`IDisposable` stops the observer** (removes the match
  rule). Always keep it (`using var` / a field) — a leaked watch is a
  leaked subscription.
- `emitOnCapturedContext: true` (default) marshals the handler to the
  captured SynchronizationContext (UI apps). In console/server code pass
  `false` to run the handler on the connection loop thread — cheaper, and
  the handler must then be fast or dispatch.
- **Handlers may run concurrently** (the library does not serialize
  deliveries); don't assume ordering inside a handler.
- Advanced form: `Notification<T>` has `Value` (the args) or, when
  `IsCompletion` is set, an `Exception` suitable for
  `TaskCompletionSource.TrySetException`. Completion kinds are selected via
  `ObserverFlags`:

  | Flag | Completion when |
  |------|-----------------|
  | `EmitOnConnectionClosed` | the connection closed |
  | `EmitOnObserverDispose` | you disposed the observer |
  | `EmitOnOwnerChanged` | the pinned owner changed (with owner-pinned destinations) |
  | `EmitOnConnectionFailed` / `EmitOnReaderFailed` | I/O / parse failure |
  | `NoSubscribe` | don't add the match rule (you already receive the messages) |
  | `EmitAll` | everything |

  For "wait until this specific signal arrives" (the job pattern), the
  simple `Action<args>` form + a `TaskCompletionSource` +
  `TrySetResult` is all you need — the example does exactly that.

### Property-change signals

`WatchPropertiesChangedAsync` delivers `IChangedFooProperties`:

- `HasXxxChanged` — true when `Xxx` was in `changed` **or** `invalidated`.
- `Xxx` — the new value as `T?`; **`null` when the peer only invalidated**
  the property (no value sent). Re-fetch with `GetXxxAsync()` in that case.

```csharp
await unit.WatchPropertiesChangedAsync(async changed =>
{
    if (changed.HasActiveStateChanged)
    {
        string state = changed.ActiveState ?? await unit.GetActiveStateAsync();
        Console.WriteLine($"ActiveState → {state}");
    }
});
```

## 6. Variants

`VariantValue` is a struct; check `.Type` (`VariantValueType`), then read
with the matching getter (`GetString()`, `GetInt32()`, `GetBool()`,
`GetDouble()`, `GetArray<T>()`, `GetDictionary<TKey,TValue>()`, ...).
Implicit conversions exist (`VariantValue v = "text";`), factories for
explicitness (`VariantValue.String(...)`), and `Array<T>` / `Dict<K,V>` /
`Struct.Create(...)` for composites. You meet variants in systemd via
`a{sv}` bags: `GetAll`, `Unit.SetProperties`, `StartTransientUnit`
properties, `Conditions`, ...

## 7. Walkthrough: `example/SystemdServiceController`

```
example/SystemdServiceController/
├── SystemdServiceController.csproj   packages + 3 × AdditionalFiles (Proxy mode)
├── dbus-xml/
│   ├── org.freedesktop.systemd1.Manager.xml   (live introspection, Manager interface only)
│   ├── org.freedesktop.systemd1.Unit.xml      (live introspection of a unit object)
│   └── org.freedesktop.systemd1.Job.xml       (hand-written from the man page; jobs are transient)
├── SystemdClient.cs                  connection + proxies + typed operations
├── Cli.cs                            subcommand front-end
└── Program.cs                        4 lines: connect, run CLI
```

### `SystemdClient` (the part worth studying)

- **Construction**: resolves `DBusAddress.System` — or `DBusAddress.Session`
  when built with `userMode: true`, the `systemctl --user` equivalent —
  failing fast with a clear message if the chosen bus is absent; builds
  `DBusConnectionOptions` with an `OnException` logger, creates the
  connection. `IAsyncDisposable` disposes watcher + connection.
- **`ConnectAsync`**: `ConnectAsync()` → `WatchNameOwnerAsync` →
  `WaitForOwnerAsync` → builds `new DBusService(connection, ownerBusName)`.
  The `NameOwnerWatcher` is kept for `GetOwnerChangedCancellationToken`
  if you want owner-flip detection.
- **Proxies on demand**: `GetService().CreateManager("/org/freedesktop/
  systemd1")` — proxies are cheap value-ish wrappers, created per call.
- **`EnsureSubscribedAsync`**: idempotent `Manager.Subscribe()` (the Part 2
  §6 gotcha) before any signal is watched.
- **`EnqueueUnitJobAsync`** — the §5 dance of Part 2, verbatim:
  1. `Subscribe()`;
  2. register `WatchJobRemovedAsync` with a `TaskCompletionSource` and a
     lock-guarded "pending job path" cell (the cell exists because the
     method-return continuation and the signal handler can run on
     different threads; D-Bus ordering guarantees the signal arrives after
     the return, but the *assignment* of the pending path must be
     published);
  3. call the verb (`StartUnitAsync(name, "replace")`, ...);
  4. publish the returned job path;
  5. `await tcs.Task.WaitAsync(timeoutToken)` — timeout wraps the
     `CancellationTokenSource` so a hung job becomes an
     `OperationCanceledException` instead of a hang.
  Result mapping: `done` → success; anything else is reported as the
  failure reason. `--no-wait` skips steps 1–2 and 5.
- **`GetUnitStatusAsync`**: `GetUnit` with `LoadUnit` fallback (a name that
  is not loaded but exists on disk still resolves; a name that exists
  nowhere comes back with `LoadState == "not-found"`), then one
  `GetPropertiesAsync()` round trip.
- **`WatchUnitAsync`**: `Subscribe()` + `Unit.WatchPropertiesChangedAsync`
  + optional `Manager.WatchJobRemovedAsync` filtered to that unit; returns
  a composite `IDisposable`.
- **`UnitName.Normalize`**: appends `.service` when no suffix is present —
  the systemd manager does **not** do this for you (unlike the `systemctl`
  CLI), so `GetUnit("nginx")` fails; the CLI convenience lives in your code.

### `Cli`

Subcommands (`list`, `files`, `status`, `start/stop/restart/reload/
try-restart [--no-wait]`, `enable/disable [--runtime] [--force]`,
`is-enabled`, `reset-failed`, `jobs`, `watch`, `version`), all thin
mappers over `SystemdClient`. A global `--user` option (accepted anywhere
on the command line, like `systemctl --user`) selects the per-user
instance: `Program.cs` strips the flag and constructs
`new SystemdClient(userMode: true)`, which switches the connection to the
session bus — the CLI logic itself is bus-agnostic. Exit codes: `0` ok, `1` error, `4` unit not
found (mirrors `systemctl`), and job verbs return non-`0` when the job
result is not `done`. `watch` installs a `Console.CancelKeyPress` handler
that cancels a linked `CancellationTokenSource` for a clean shutdown.
Error text is mapped from `DBusErrorReplyException.ErrorName`
(`FailBusError`) — names, not messages, are the contract.

### Build & run (verified on systemd 259, Ubuntu)

```bash
cd example/SystemdServiceController
dotnet build
dotnet run -- list colord                 # read: works unprivileged
dotnet run -- status colord               # read
dotnet run -- files colord                # read
dotnet run -- jobs
dotnet run -- watch colord                # read + signals (Ctrl+C to stop)
sudo dotnet run -- restart colord         # write: needs polkit manage-units
dotnet run -- start colord --no-wait      # unprivileged: fails fast with
                                           # InteractiveAuthorizationRequired → friendly message
dotnet run -- --user list                 # user instance: session bus,
                                           # per-user units (no polkit for own units)
dotnet run -- --user status gpg-agent     # a typical user-space unit
```

## 8. Static codegen vs source generator (the repo-root `Systemd.DBus.cs`)

`Systemd.DBus.cs` was generated with:

```
dotnet dbus codegen --protocol-api \
  --namespace Systemd.DBus \
  dbus-xml/org.freedesktop.systemd1.Manager.xml
```

Differences to be aware of:

- **Static files** you commit; the **source generator** regenerates each
  build. The library docs recommend the source generator for
  `Tmds.DBus.Protocol`.
- The static tool output is **`partial`** by design: `sealed partial class
  Manager` + `static partial class ObjectFactory` so you can extend it in
  your own namespace. Note its `using SystemdServiceController;` and the
  `JobRemovedArgs` record: for the 4-argument `JobRemoved` signal the static
  tool materializes a named record referenced from the consumer namespace
  (not defined inside the generated file), while the source generator
  inlines a named tuple type
  (`(uint Id, ObjectPath Job, string Unit, string Result)`).
- The repo-root file was generated from a **trimmed** Manager XML (4 verbs +
  `GetUnit` + 4 signals). It is *not* the full systemd surface (no
  `ListUnits`, `EnableUnitFiles`, `Subscribe`, properties, ...). The
  example's `dbus-xml/` files replace it; keep both as references.

## 9. Pitfalls checklist (all hit for real)

1. **`Subscribe()` before watching** — systemd emits most signals (incl.
   unit `PropertiesChanged` and `JobRemoved`) only while a client has
   subscribed. Silent no-op otherwise.
2. **Match rule before the call** — install `WatchJobRemovedAsync` *before*
   `StartUnitAsync`, then filter by the returned job path (ordered
   delivery makes this race-free).
3. **`DBusService` is a struct** — `Nullable<DBusService>` cannot call the
   `Create*` extensions; keep a non-nullable local when dereferencing a
   nullable field.
4. **Generated call methods take no `CancellationToken`** — put timeouts
   around them (`Task.WaitAsync(TimeSpan, ct)`) if you need a bound.
5. **`EmitCompilerGeneratedFiles` inside the project root = CS0101 storm**
   (double compilation). Emit under `obj/`.
6. **Struct-array return types** — `ListUnitsAsync()` returns a tuple
   *array*: use `.Length`, deconstruct, or map to records immediately.
7. **Unit names are not mangled by the manager** — `"nginx"` ≠
   `"nginx.service"` over D-Bus; normalize like the example.
8. **Object paths are encoded** — prefer `GetUnit`/`LoadUnit` over building
   paths; if you must, encode every non-alphanumeric as `_xx`.
9. **Invalidated properties are `null`** — `HasXxxChanged` true does not
   imply a value; re-fetch when null.
10. **Polkit fail-fast** — Tmds.DBus does not request interactive
    authorization; expect
    `org.freedesktop.DBus.Error.InteractiveAuthorizationRequired` (not a
    25-second hang) for unprivileged mutations. Branch on `ErrorName`.
11. **Handlers are not serialized** — keep them fast; dispatch long work.
    And a handler that throws can disconnect the connection — never let
    exceptions escape a signal handler.
12. **`Version` is not an API** — display it, never parse it.
