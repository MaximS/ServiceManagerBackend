# ServiceManagerBackend — non-obvious decisions and notes

Everything here is either a .NET 10 / D-Bus / systemd gotcha or a deliberate choice that
isn't visible from the API surface.

## D-Bus / systemd layer

### One connection, lazy, for the whole app lifetime
`SystemdClient` is a DI singleton holding a single session-bus connection. It connects
**lazily on the first request** (`EnsureConnectedAsync`, lock-guarded, connect-once).
Reason: the app can start and serve `/swagger` + `/openapi/v1.json` even if the session
bus is not up yet (e.g. container without logind); the first real call surfaces the
actual error instead of failing at boot.

### `Subscribe()` before any signal
`Manager.Subscribe()` is a per-connection flag. Until it is called, systemd delivers
**no signals at all** (no `JobRemoved`, no `PropertiesChanged`) and fails silently.
`EnsureSubscribedAsync()` runs idempotently inside every code path that installs a
watcher — this is the #1 classic bug in this stack.

### Watch-before-call job pattern
For every start/stop the `JobRemoved` watcher is installed **before** the `StartUnit`/
`StopUnit` call. A fast job can emit `JobRemoved` immediately after the method reply;
if the watcher is installed after, the completion is lost and the request hangs until
timeout. The pending job path is a lock-guarded cell written after the reply — D-Bus
message ordering + the lock close the race (same pattern as the verified reference CLI).

### Job result ≠ unit health
A job finishing with `result: "done"` does **not** mean the service is healthy:
for `Type=simple` units the start job completes at `exec()`, and a binary that
exits non-zero afterwards leaves the unit in `failed` state while the job was `done`
(verified live). Conversely, real job-level failures come as `result: "failed"`,
`"dependency"`, `"skipped"`, `"timeout"` — or as a D-Bus error reply (e.g.
`org.freedesktop.systemd1.NoSuchUnit` when a `Requires=` dependency doesn't exist).
The API treats anything not `done` as failure, but "done" ≠ "running".

### `GetUnit` → `LoadUnit` fallback
`GetUnit` returns `not-found` for units that have a unit file but are not loaded yet.
`GetUnitStatusAsync` falls back to `LoadUnit` on a D-Bus error reply, so a never-started
service still yields a real status instead of a 404.

## HTTP / API layer

### SSE over POST
`monitor-start` / `monitor-stop` are POST endpoints that answer
`Content-Type: text/event-stream`. This is non-standard (the browser `EventSource` API
only does GET), but it keeps the start/stop verbs semantically POST while streaming
progress. Clients: `curl -N -X POST ...` or any HTTP client with streaming bodies.
Swagger UI shows the first chunk; for full streaming use curl or a real SSE client.

### SSE frame ordering
Frames: `job-enqueued`, `state-changed` (0..n), then exactly one of `job-completed` /
`error`, after which the stream closes. Note: systemd can emit the first
`PropertiesChanged` (job assignment) **before** it sends the `StartUnit` method reply,
so a `state-changed` frame may precede `job-enqueued`. `job-completed`/`error` is
always the last frame.

### Timeout vs client disconnect
Waiting endpoints distinguish "our wait timeout fired" (→ 504) from "the client went
away" (→ abort, no response) by checking `HttpContext.RequestAborted` in the
`OperationCanceledException` filter. Same inside the SSE producer: on timeout it writes
an `error` frame; on client disconnect it just closes.

### Job mode: `replace`
Start/stop enqueues with mode `replace` (systemctl default): a new job replaces a
conflicting queued one instead of failing. If you want orchestrator-grade strictness,
switch `SystemdClient.DefaultJobMode` to `fail`.

### Whitelist + 404 semantics
Only services listed in `ServiceManager:Services` are addressable; anything else is 404
`ServiceNotWhitelisted`. A whitelisted service whose unit file doesn't exist is also
404 (`UnitNotFound`). Names are normalized client-side (`.service` appended when no
dot is present) and compared case-sensitively.

### start-all / stop-all
Strictly sequential, each step waits for its job, fail-fast: the first non-`done`
result (or D-Bus error) returns 500 with `{failedService, result, completed[]}`.
They respond only on completion (no SSE) — agreed during planning.

## .NET 10 specifics (bitten while building this)

- **`WithOpenApi(op => ...)` is deprecated (ASPDEPR002)** in .NET 10. Per-route OpenAPI
  customization now goes through `AddOpenApiOperationTransformer((op, ctx, ct) => ...)`
  (endpoint-specific operation transformers, new in .NET 10).
- **`OptionsBuilder.Validate` no longer accepts a rich result type.** The
  `Func<T, ValidationResult>` overload is gone; use an `IValidateOptions<T>`
  implementation returning `ValidateOptionsResult` (new type, .NET 9+) plus
  `.ValidateOnStart()`.
- **`Results.Stream` has no `IAsyncEnumerable` overload.** It takes
  `Func<Stream, Task>` / `PipeReader` / `Stream`. The SSE producer is therefore pumped
  through an unbounded `Channel<byte[]>` and written with explicit `FlushAsync` per
  frame (flushing is what makes SSE real-time).
- **`Results.StatusCode(int, object)` does not exist** — arbitrary status + JSON body is
  `Results.Json(body, statusCode: N)`.
- **`Swashbuckle.AspNetCore.SwaggerUI` 10.x**: the extension is `UseSwaggerUI` (capital
  UI) and the option is `options.SwaggerEndpoint("/openapi/v1.json", "v1")` — the
  NSwag-style `DocumentPath` does not exist. The OpenAPI *document* itself comes from
  the built-in `Microsoft.AspNetCore.OpenApi` (`app.MapOpenApi()`), not from Swashbuckle.
- **`Microsoft.OpenApi` 2.x (bundled with .NET 10)**: model types moved from
  `Microsoft.OpenApi.Models` to the root `Microsoft.OpenApi` namespace.
- `Tmds.DBus.Generator` emits proxies from `dbus-xml/*.xml` via `AdditionalFiles`;
  `CompilerGeneratedFilesOutputPath` must stay under `$(BaseIntermediateOutputPath)`
  or the emitted .cs gets compiled twice (CS0101 storm).

## Error mapping (D-Bus → HTTP)

| D-Bus error name | HTTP |
|---|---|
| `org.freedesktop.systemd1.UnitNotFound` | 404 |
| `org.freedesktop.systemd1.UnitMasked` | 409 |
| `org.freedesktop.systemd1.UnitNotActive` | 409 |
| `org.freedesktop.systemd1.NoSuchUnit` (broken dependency) | 502 |
| `org.freedesktop.DBus.Error.AccessDenied` / polkit | 403 (shouldn't happen on the session bus) |
| `org.freedesktop.DBus.Error.NoReply` | 504 |
| job wait timeout (our side) | 504 |
| anything else | 500 |

## What was verified live (user bus, `systemctl --user` equivalent)

- `GET /api/services`, `GET /api/services/{name}` — correct states, 404 for non-whitelisted.
- `POST .../start` / `.../stop` — job `done`, states transition verified via the API.
- `POST .../monitor-stop` — full SSE trace observed:
  `active/running → deactivating/stop-sigterm → inactive/dead → job-completed(done)`.
- `POST /start-all` fail-fast — a unit with `Requires=does-not-exist-xyz.service`
  broke the sequence: `500 {failedService, result: NoSuchUnit, completed: [...]}`.
- `/swagger` UI and `/openapi/v1.json` (8 paths) both 200 in Production environment.

A demo unit `example-sleep.service` (sleep 300) exists in `~/.config/systemd/user/` —
remove it once you configure your real services in `appsettings.json`.
Run the app with `dotnet run` in `ServiceManagerBackend/` (or `dotnet run --urls
http://127.0.0.1:5199`); SwaggerUI is at `http://127.0.0.1:5199/swagger`.
