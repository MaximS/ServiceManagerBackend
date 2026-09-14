# Experiment on Agentic Coding
- LLM - Qwen3.8:Q8_0
- Engine - llama.cpp
- Harness - OpenCode
- Task - Create dotnet webapi to control systemd services via D-Bus

## Process
1. Phase 1 - Research on D-Bus and TMDS library. Result - SKILL.md and three additional artifacts.
2. Phase 2 - Create a dotnet webapi backend

## Backend features
- Works with user space services only
- Two variants for starting and stopping services
-- Waiting for completion - ubtil the job is done
-- Streamig intermediate statuses via SSE
- Logging is explicitly disabled to keep logic clearly visible

*** No manual code and docs modifications ***

-------------------------------------------------------
The following is original SKILL.md created by the model
-------------------------------------------------------

# Controlling systemd over D-Bus from .NET

Reference bundle for building .NET tools that control systemd (PID 1)
through its D-Bus API (`org.freedesktop.systemd1`) using the
[Tmds.DBus](https://tmds.github.io/Tmds.DBus/) library.

## Reading order

| # | Document | For |
|---|----------|-----|
| 1 | [`01-dbus-fundamentals.md`](01-dbus-fundamentals.md) | D-Bus from zero: buses, names/paths/interfaces/members, message types, signatures, the standard Properties interface, introspection, security, `busctl` toolbox |
| 2 | [`02-systemd-dbus-api.md`](02-systemd-dbus-api.md) | The systemd API: object model, path encoding, Manager/Unit/Job interfaces, job modes and results, the job-completion pattern, the `Subscribe()` gotcha, polkit, error names |
| 3 | [`03-tmddbus-library.md`](03-tmddbus-library.md) | Tmds.DBus: packages, connections, the XML → C# source generator, proxies/signals/variants/owner-watcher, walkthrough of the example project, pitfalls checklist |

## Runnable example

`example/SystemdServiceController/` — a console CLI that does what
`systemctl` does, over D-Bus, with typed generated proxies:
`list`, `files`, `status`, `start/stop/restart/reload/try-restart`
(with job-completion waiting), `enable/disable`, `is-enabled`,
`reset-failed`, `jobs`, `watch` (live `PropertiesChanged` + `JobRemoved`),
`version`.

```bash
cd example/SystemdServiceController
dotnet build
dotnet run -- help
```

Read-only commands work unprivileged; mutating commands need root or the
polkit actions `org.freedesktop.systemd1.manage-units` /
`.manage-unit-files` (Part 2, §7).

> **User-space systemd (addendum).** All commands accept a global `--user`
> flag, the equivalent of `systemctl --user`:
>
> ```bash
> dotnet run -- --user list          # units of the per-user instance
> dotnet run -- --user start myapp   # unit file in ~/.config/systemd/user/
> ```
>
> `--user` switches the *bus only* — from the system bus
> (`DBusAddress.System`, PID 1) to the session bus (`DBusAddress.Session`,
> the per-user systemd instance started by systemd-logind at login). The
> well-known name, object paths, interfaces, and the whole job-completion
> pattern are identical (Parts 1–2 all apply unchanged). Differences to
> keep in mind: the instance requires a running session bus (i.e.
> `$XDG_RUNTIME_DIR` set — no `--user` in headless containers or cron
> unless you set it up); its unit files live in
> `~/.config/systemd/user/`; and the session owner can manage their own
> units without polkit authentication, because the session bus is already
> restricted to that user.

## Other files in this repo

- `Systemd.DBus.cs` (repo root) — a **statically generated** Manager proxy
  (produced by `dotnet dbus codegen --protocol-api` from a trimmed XML:
  4 unit verbs + `GetUnit` + 4 signals). Incomplete by design; kept as a
  reference for what the static codegen path looks like. See Part 3, §8.
- `SKILL.md` — machine-oriented instructions for a coding agent to scaffold
  a new project of this kind from scratch.

## Source material

- Tmds.DBus documentation: https://tmds.github.io/Tmds.DBus/ (main page,
  `Tmds.DBus.Protocol` API reference).
- systemd D-Bus man page: `org.freedesktop.systemd1(5)`
  (https://www.freedesktop.org/software/systemd/man/latest/org.freedesktop.systemd1.html)
  — the authoritative API description, incl. introspection skeletons.
- D-Bus specification: https://dbus.freedesktop.org/doc/dbus-specification.html
- Verified live against systemd **259** (`busctl introspect`) on Ubuntu.
