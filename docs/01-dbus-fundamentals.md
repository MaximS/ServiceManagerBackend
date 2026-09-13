# D-Bus Fundamentals

> Part 1 of 3. This document explains the D-Bus IPC system from first principles,
> assuming you know C#/.NET but have never touched D-Bus. It uses systemd as the
> running example throughout.

## 1. What D-Bus is

D-Bus (Desktop Bus) is a **message bus system**: a broker process that lets
independent processes send each other typed messages without knowing each
other's network addresses. Think of it as a local, typed, permissioned RPC +
pub/sub system that ships with every Linux distribution.

One broker process (the **bus daemon**) owns a single socket. Every interested
process opens a connection to it. The daemon routes messages between
connections, enforces an access-control **policy**, and can even start
("activate") services on demand when a message is addressed to them.

There are (usually) two separate buses on a machine:

| Bus | Socket | Who talks on it | Examples |
|-----|--------|-----------------|----------|
| **System bus** | `/run/dbus/system_bus_socket` | daemons / privileged tools | `systemd` (PID 1), NetworkManager, UPower, logind |
| **Session bus** | `$XDG_RUNTIME_DIR/bus` | one per logged-in user | media players (MPRIS), notification daemon, settings daemons |

Each bus has its own set of registered services and its own policy. **systemd
lives on the system bus.** The Tmds.DBus library reaches a bus by *address*
(a string like `unix:path=/run/dbus/system_bus_socket`); the standard helpers
`DBusAddress.System` and `DBusAddress.Session` return the right address for
the current machine, or `null` if that bus does not exist (e.g. no
`$XDG_RUNTIME_DIR` in a headless container).

> **Note (addendum): the user instance also speaks D-Bus.** Every logged-in
> user has a *second* systemd — the per-user instance (not PID 1) that
> `systemctl --user` talks to. It registers the **same** well-known name
> `org.freedesktop.systemd1`, with the same object model and API, on the
> **session bus** (`$XDG_RUNTIME_DIR/bus`). So "which bus" is the only
> coordinate that changes; everything in this document applies to both.
> The rest of this bundle focuses on the system instance; treat the user
> instance as a side note.

## 2. The five parts of a D-Bus "address"

Every message is delivered to a **well-known name** (the *service*), and every
method/signal is located by three more coordinates. Together they form the
address of a single operation:

```
destination (bus name)   org.freedesktop.systemd1          ← which service
object path              /org/freedesktop/systemd1         ← which object
interface                org.freedesktop.systemd1.Manager  ← which "class"
member                   StartUnit                         ← which method
```

Read that as object-oriented navigation:
"on service *org.freedesktop.systemd1*, at object
*/org/freedesktop/systemd1*, on interface *...Manager*, call *StartUnit*."

### 2.1 Names (the "who")

Two kinds of names:

- **Well-known name** (a.k.a. bus name): a human-chosen dotted name like
  `org.freedesktop.systemd1`. Multiple processes can *request* it, but only
  one owns it at a time. Clients address messages to the well-known name; the
  bus daemon resolves it to the current owner and delivers the message there.
  When the owner goes away and another process takes the name, your next
  message simply follows the new owner. (The Tmds.DBus
  `NameOwnerWatcher` exists for the rare case where you want to pin messages
  to one specific owner instance.)
- **Unique name**: assigned by the bus to every connection, e.g. `:1.42`.
  You cannot choose it; it identifies *your* connection.

### 2.2 Object paths (the "where")

A slash-separated path that looks like a filesystem path but is just a
namespace: `/org/freedesktop/systemd1`. A single service exposes *many*
objects, each at its own path:

```
/org/freedesktop/systemd1                     → the Manager object
/org/freedesktop/systemd1/unit/ssh_2eservice  → the ssh.service unit object
/org/freedesktop/systemd1/job/4711            → job #4711
```

### 2.3 Interfaces (the "what kind of thing")

An object can implement several **interfaces** at once — conceptually the
equivalent of a C# object implementing multiple interfaces. An interface is a
dotted name (`org.freedesktop.systemd1.Unit`) that groups related methods,
signals, and properties. The *same* object path often carries:

- a generic interface (`...Unit`) plus a type-specific one (`...Service`);
- the standard interfaces every object has:
  `org.freedesktop.DBus.Peer`, `...Introspectable`, `...Properties`.

### 2.4 Members (the "which operation")

The method/signal/property name within an interface: `StartUnit`,
`PropertiesChanged`, `ActiveState`.

> **Why four levels?** Because it is a distributed object model that has to
> be unambiguous across independently-developed services. In practice you
> mostly just need: *name* (which daemon), *path* (which thing), *interface*
> (which API), *member* (which call).

## 3. The three message types that matter

D-Bus has four message types; three matter for a client:

1. **Method call** — a request. Has a reply cookie. Exactly one of the next
   two will come back for it.
2. **Method return** — the successful reply, carrying return values.
3. **Error** — the failure reply, carrying a structured **error name** (a
   dotted string, e.g. `org.freedesktop.systemd1.UnitNotFound`) plus a
   human-readable message.
4. **Signal** — a fire-and-forget broadcast. No reply, one-to-many. The
   receiver subscribes by installing a **match rule** with the bus daemon.

```
   client                         bus daemon                     systemd (PID 1)
     |--- method call: StartUnit -->|                               |
     |                              |--- routed to owner --------->|
     |                              |                               | enqueue job
     |<------------------------------|--- method return: job path --|
     |                              |                               |
     |<------------------------------|--- signal: JobRemoved ------|  (later)
```

**Method calls are synchronous in spirit** (you await a reply) and
**signals are asynchronous** (you register a handler and it is invoked on a
background loop when the message arrives). This split is the heart of
controlling systemd from .NET: you *call* `StartUnit`, it *returns* a job
path immediately, and the actual completion arrives later as a *signal*.

## 4. Types and signatures

D-Bus messages carry a **signature**: a compact string describing the body's
type layout, and the body itself, binary-encoded (big-endian, with
alignment). You never encode by hand in .NET — the library does — but you
must *read* signatures, because they appear in introspection XML, in
`busctl` output, and in the generated C# types.

Basic types (one character each):

| Sig | D-Bus type           | .NET type (Tmds.DBus)      |
|-----|----------------------|----------------------------|
| `y` | byte                 | `byte`                     |
| `b` | boolean              | `bool`                     |
| `n` | int16                | `short`                    |
| `q` | uint16               | `ushort`                   |
| `i` | int32                | `int`                      |
| `u` | uint32               | `uint`                     |
| `x` | int64                | `long`                     |
| `t` | uint64               | `ulong`                    |
| `d` | double               | `double`                   |
| `s` | UTF-8 string         | `string`                   |
| `o` | object path          | `ObjectPath`               |
| `g` | signature            | `Signature`                |
| `h` | file descriptor      | `SafeHandle`               |
| `v` | **variant** (any type) | `VariantValue`          |

Composite types (built from the basics):

| Sig | Meaning | Example |
|-----|---------|---------|
| `a<elt>` | array of `elt` | `as` = string[] |
| `a{<k><v>}` | dict of `k`→`v` | `a{sv}` = Dictionary<string, Variant> (the "property bag") |
| `(<t>...)` | struct (tuple) | `(so)` = (string, ObjectPath) |

So `a(ssssssouso)` — the return type of `ListUnits` — is an **array of
10-field structs**, each field being a string/string/.../object-path.
Tmds.DBus's generator maps that to a C# tuple array:
`(string, string, string, string, string, string, ObjectPath, uint, string, ObjectPath)[]`.

The **variant** (`v`) is the escape hatch: a boxed value of any D-Bus type.
It appears everywhere systemd exposes heterogeneous data, most famously in
`a{sv}` "property bags" (e.g. `GetAll` on the standard Properties interface,
or unit configuration). In Tmds.DBus a variant is a `VariantValue` struct you
inspect via `.Type` then read with the matching `Get*()` method.

## 5. The standard `org.freedesktop.DBus.Properties` interface

Every object that exposes properties implements this *standard* interface,
which gives you three generic methods and one signal — you use these to read
state without needing a dedicated method per property:

```
Get(   in  s interface_name, in  s property_name, out v value)
GetAll(in  s interface_name,                out a{sv} props)
Set(   in  s interface_name, in  s property_name, in v value)

PropertiesChanged(in s interface_name, in a{sv} changed, in as invalidated)
```

For systemd you will mostly read properties like this:
interface `org.freedesktop.systemd1.Unit`, property `ActiveState` →
`"active"`. The generator wraps `Get`/`GetAll` into strongly-typed
`GetActiveStateAsync()` / `GetPropertiesAsync()` calls, so you rarely touch
the raw interface — but understanding it explains where those methods come
from.

`PropertiesChanged` is the workhorse *signal* for "state moved". Note the
`invalidated` list: a service may tell you a property changed *without*
sending the new value (you must re-`Get` it). The Tmds.DBus generator models
this with `IChanged...Properties` where each changed property is `T?` and can
be `null` when only invalidated.

## 6. Introspection: reading a service's API at runtime

Any object can be asked for its own API description:

```
gdbus introspect --system --dest org.freedesktop.systemd1 \
                 --object-path /org/freedesktop/systemd1
# or
busctl introspect org.freedesktop.systemd1 /org/freedesktop/systemd1
```

This calls the standard `org.freedesktop.DBus.Introspectable.Introspect()`
method, which returns an **XML document** describing every interface, method,
signal, and property at that path — including the signatures. This XML is
exactly what you feed to the Tmds.DBus source generator (Part 3).

Key consequence: **you do not need vendor-provided bindings.** You can
introspect a live systemd, save the XML, and have the C# proxy types
generated. (The `dbus-xml/*.xml` files in `example/` were produced exactly
this way.)

## 7. Security model (why `systemctl` "just works" and your app may not)

The bus daemon enforces a **policy** (XML in `/usr/share/dbus-1/` and
`/etc/dbus-1/system.d/`). The systemd service ships
`org.freedesktop.systemd1.conf`, which roughly says:

- **Anyone** may *read* (call query methods, read properties, receive
  signals). That is why `list`/`status` work unprivileged.
- **Privileged operations** (start/stop/enable/kill/...) are gated by
  **polkit** (`org.freedesktop.systemd1.manage-units`,
  `...manage-unit-files`, ...). Root is auto-allowed. A normal user is
  allowed only if a polkit rule grants it, or if interactive authentication
  is enabled and the user authenticates.

Practical upshot for your .NET app:

- Read-only commands: work as any user.
- Mutating commands: need `root`, a polkit rule, or an authenticating
  agent. Tmds.DBus does **not** enable "interactive authorization" by
  default, so an unprivileged `StartUnit` fails fast with
  `org.freedesktop.DBus.Error.InteractiveAuthorizationRequired` rather than
  prompting. Run mutating commands under `sudo` unless you deliberately
  wire up polkit.

> **Addendum (user instance).** The polkit dance above is a *system-bus*
> story. On the **session bus**, only the session owner can connect at all,
> so the per-user systemd instance lets its owner manage their own units
> with no extra authentication — which is exactly why `systemctl --user
> start foo` just works without a password.

Error names you will actually meet:

| Error name | Meaning |
|------------|---------|
| `org.freedesktop.systemd1.UnitNotFound` | no unit by that name |
| `org.freedesktop.systemd1.UnitMasked` | unit is masked (→ `/dev/null`) |
| `org.freedesktop.systemd1.UnitNotActive` | operation requires an active unit |
| `org.freedesktop.DBus.Error.AccessDenied` | policy denied the call |
| `org.freedesktop.DBus.Error.InteractiveAuthorizationRequired` | needs polkit auth, not enabled by caller |
| `org.freedesktop.DBus.Error.Failed` | generic failure |
| `org.freedesktop.DBus.Error.UnknownObject` | bad object path |

## 8. Hands-on: the `busctl` / `gdbus` toolbox

Before you write any C#, learn to drive systemd by hand. These are the
commands you will mirror in code:

```bash
# List all names on the system bus (confirm systemd is there).
busctl --no-pager list | grep systemd1

# Introspect an object (dump the XML API).
busctl --no-pager --xml introspect org.freedesktop.systemd1 \
  /org/freedesktop/systemd1

# Call a method. Signature first ("s" = one string arg), then the value.
busctl --no-pager call org.freedesktop.systemd1 /org/freedesktop/systemd1 \
  org.freedesktop.systemd1.Manager GetUnit s ssh.service

# Read a single property.
busctl --no-pager get-property org.freedesktop.systemd1 \
  /org/freedesktop/systemd1/unit/ssh_2eservice \
  org.freedesktop.systemd1.Unit ActiveState

# Read all properties of an interface.
busctl --no-pager get-property org.freedesktop.systemd1 \
  /org/freedesktop/systemd1/unit/ssh_2eservice org.freedesktop.systemd1.Unit

# Mutate (needs privilege): StartUnit(name, mode) → returns job path.
sudo busctl --no-pager call org.freedesktop.systemd1 /org/freedesktop/systemd1 \
  org.freedesktop.systemd1.Manager StartUnit ss ssh.service replace

# Watch everything in real time (method calls, returns, errors, signals).
busctl --no-pager monitor org.freedesktop.systemd1
```

Once you can do all of these from the shell, the C# is just a typed,
awaitable re-statement of the same four-part address.

## 9. Mental model in one picture

```
        system bus daemon (/run/dbus/system_bus_socket)
        ┌────────────────────────────────────────────────────────────┐
        │  policy: reads=everyone, writes=polkit                     │
        │                                                            │
  you ──┤  name: org.freedesktop.systemd1                            │
 (your  │   ├── /org/freedesktop/systemd1        [Manager]          │
  .NET  │   │       └─ StartUnit(name,mode) → job path              │
  app)  │   ├── /org/freedesktop/systemd1/unit/ssh_2eservice        │
        │   │       └─ [Unit] ActiveState, SubState, ...            │
        │   └── /org/freedesktop/systemd1/job/4711                  │
        │           └─ [Job]  JobType, State;  signal JobRemoved    │
        └────────────────────────────────────────────────────────────┘
                                  ▲ owned by
                                  │
                             systemd (PID 1)
```

**Next:** Part 2 maps this to systemd's concrete API (objects, methods,
states, and the job/signals dance). Part 3 covers the Tmds.DBus library that
turns the introspection XML into the C# you call.
