# Graph Report - .  (2026-09-13)

## Corpus Check
- Corpus is ~11,307 words - fits in a single context window. You may not need a graph.

## Summary
- 163 nodes · 313 edges · 15 communities (12 shown, 3 thin omitted)
- Extraction: 97% EXTRACTED · 3% INFERRED · 0% AMBIGUOUS · INFERRED: 8 edges (avg confidence: 0.94)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_D-Bus Core Concepts|D-Bus Core Concepts]]
- [[_COMMUNITY_CLI Commands|CLI Commands]]
- [[_COMMUNITY_D-Bus Semantics & Job Flow|D-Bus Semantics & Job Flow]]
- [[_COMMUNITY_systemd D-Bus API|systemd D-Bus API]]
- [[_COMMUNITY_SystemdClient Connection|SystemdClient Connection]]
- [[_COMMUNITY_Unit Watching & Results|Unit Watching & Results]]
- [[_COMMUNITY_Manager Queries|Manager Queries]]
- [[_COMMUNITY_Job Enqueue & Lifecycle|Job Enqueue & Lifecycle]]
- [[_COMMUNITY_EnableDisable Units|Enable/Disable Units]]
- [[_COMMUNITY_Unit Status Queries|Unit Status Queries]]
- [[_COMMUNITY_Project & Dependencies|Project & Dependencies]]
- [[_COMMUNITY_Pending Job Cache|Pending Job Cache]]
- [[_COMMUNITY_D-Bus Members|D-Bus Members]]
- [[_COMMUNITY_Unique Bus Names|Unique Bus Names]]

## God Nodes (most connected - your core abstractions)
1. `SystemdClient` - 27 edges
2. `Cli` - 21 edges
3. `CancellationToken` - 13 edges
4. `CancellationToken` - 12 edges
5. `Task` - 12 edges
6. `Task` - 11 edges
7. `systemd-dbus-dotnet skill` - 7 edges
8. `org.freedesktop.systemd1.Manager interface` - 7 edges
9. `Reference bundle README` - 7 edges
10. `D-Bus Fundamentals (Part 1)` - 6 edges

## Surprising Connections (you probably didn't know these)
- `example/SystemdServiceController project` --semantically_similar_to--> `example/SystemdServiceController reference project`  [INFERRED] [semantically similar]
  docs/03-tmddbus-library.md → SKILL.md
- `SystemdClient.EnqueueUnitJobAsync implementation` --semantically_similar_to--> `EnqueueUnitJobAsync race-free job pattern`  [INFERRED] [semantically similar]
  docs/03-tmddbus-library.md → SKILL.md
- `User-space systemd addendum (session bus, --user)` --semantically_similar_to--> `User instance (systemctl --user)`  [INFERRED] [semantically similar]
  SKILL.md → docs/01-dbus-fundamentals.md
- `EnqueueUnitJobAsync race-free job pattern` --references--> `Job-completion dance`  [INFERRED]
  SKILL.md → docs/02-systemd-dbus-api.md
- `Reference bundle README` --references--> `systemd-dbus-dotnet skill`  [EXTRACTED]
  docs/README.md → SKILL.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Job-completion flow: Subscribe, watch JobRemoved, call StartUnit, match job path** — docs_02_systemd_dbus_api_subscribe, docs_02_systemd_dbus_api_jobremoved, docs_02_systemd_dbus_api_startunit, docs_02_systemd_dbus_api_job_completion_dance, docs_03_tmddbus_library_enqueueunitjobasync, skill_enqueueunitjobasync [EXTRACTED 1.00]
- **org.freedesktop.systemd1 object model (Manager/Unit/Job)** — docs_02_systemd_dbus_api_org_freedesktop_systemd1, docs_02_systemd_dbus_api_manager, docs_02_systemd_dbus_api_unit, docs_02_systemd_dbus_api_job [EXTRACTED 1.00]
- **Four-part D-Bus operation address (name, path, interface, member)** — docs_01_dbus_fundamentals_well_known_name, docs_01_dbus_fundamentals_object_path, docs_01_dbus_fundamentals_interface, docs_01_dbus_fundamentals_member [EXTRACTED 1.00]

## Communities (15 total, 3 thin omitted)

### Community 0 - "D-Bus Core Concepts"
Cohesion: 0.11
Nodes (30): D-Bus Fundamentals (Part 1), Bus daemon, busctl / gdbus toolbox, D-Bus message bus, D-Bus interface, D-Bus introspection, Session bus, Signal (fire-and-forget broadcast) (+22 more)

### Community 1 - "CLI Commands"
Cohesion: 0.20
Nodes (7): DBusErrorReplyException, CancellationToken, Task, UnitState, UnitStatus, Exception, Cli

### Community 2 - "D-Bus Semantics & Job Flow"
Cohesion: 0.10
Nodes (25): Method call / return / error, Bus access-control policy, polkit authorization, PropertiesChanged signal, org.freedesktop.DBus.Properties interface, D-Bus type signature, Variant type (v) / a{sv} property bag, D-Bus error names systemd emits (+17 more)

### Community 3 - "systemd D-Bus API"
Cohesion: 0.19
Nodes (13): Object path, EnableUnitFiles / DisableUnitFiles, Manager.GetUnit / LoadUnit, org.freedesktop.systemd1.Job interface, Job mode strings (fail/replace/isolate/...), Manager.ListUnits, org.freedesktop.systemd1.Manager interface, org.freedesktop.systemd1 service (+5 more)

### Community 4 - "SystemdClient Connection"
Cohesion: 0.22
Nodes (9): bool, DBusConnection, DBusService, IAsyncDisposable, NameOwnerWatcher, string, SystemdClient, UnitName (+1 more)

### Community 5 - "Unit Watching & Results"
Cohesion: 0.27
Nodes (7): Action, CancellationToken, UnitState, JobId, Result, Unit, ValueTask

### Community 6 - "Manager Queries"
Cohesion: 0.24
Nodes (5): IReadOnlyList, JobInfo, Manager, UnitFile, UnitInfo

### Community 7 - "Job Enqueue & Lifecycle"
Cohesion: 0.25
Nodes (4): IDisposable, JobOutcome, UnitWatch, UnitJob

### Community 8 - "Enable/Disable Units"
Cohesion: 0.38
Nodes (5): CarriesInstallInfo, Changes, Destination, Kind, Symlink

### Community 10 - "Project & Dependencies"
Cohesion: 0.33
Nodes (4): net10.0, Tmds.DBus.Generator (0.95.1), Tmds.DBus.Protocol (0.95.1), Microsoft.NET.Sdk

### Community 11 - "Pending Job Cache"
Cohesion: 0.60
Nodes (3): object, ObjectPath, PendingJob

## Knowledge Gaps
- **39 isolated node(s):** `UnitStatus`, `UnitState`, `Exception`, `DBusErrorReplyException`, `TimeSpan` (+34 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **3 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `SystemdClient` connect `SystemdClient Connection` to `Unit Watching & Results`, `Manager Queries`, `Job Enqueue & Lifecycle`, `Enable/Disable Units`, `Unit Status Queries`, `Pending Job Cache`?**
  _High betweenness centrality (0.058) - this node is a cross-community bridge._
- **Why does `org.freedesktop.systemd1 service` connect `systemd D-Bus API` to `D-Bus Core Concepts`?**
  _High betweenness centrality (0.030) - this node is a cross-community bridge._
- **Why does `org.freedesktop.systemd1.Manager interface` connect `systemd D-Bus API` to `D-Bus Core Concepts`, `D-Bus Semantics & Job Flow`?**
  _High betweenness centrality (0.027) - this node is a cross-community bridge._
- **What connects `UnitStatus`, `UnitState`, `Exception` to the rest of the system?**
  _40 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `D-Bus Core Concepts` be split into smaller, more focused modules?**
  _Cohesion score 0.1103448275862069 - nodes in this community are weakly interconnected._
- **Should `D-Bus Semantics & Job Flow` be split into smaller, more focused modules?**
  _Cohesion score 0.1 - nodes in this community are weakly interconnected._