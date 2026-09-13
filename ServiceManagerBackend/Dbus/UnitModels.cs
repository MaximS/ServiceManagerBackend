using Tmds.DBus.Protocol;

namespace ServiceManagerBackend.DBus;

/// <summary>Job kinds exposed by the API. Mirrors the verbs used against org.freedesktop.systemd1.</summary>
public enum UnitJob
{
    Start,
    Stop,
}

/// <summary>Full state of a single unit, fetched in one D-Bus round trip (GetAll).</summary>
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

/// <summary>Incremental state update from a unit PropertiesChanged signal.</summary>
public sealed record UnitStateChange(
    string? LoadState,
    string? ActiveState,
    string? SubState,
    string? UnitFileState,
    (uint Id, ObjectPath Path)? Job);

/// <summary>
/// Outcome of a unit job. <see cref="Result"/> is null when the job was enqueued
/// without waiting; otherwise it is the systemd job result string
/// ("done", "failed", "canceled", "timeout", "dependency", "skipped").
/// </summary>
public sealed record JobOutcome(
    uint JobId,
    ObjectPath JobPath,
    string? Result);

/// <summary>Normalizes user-facing service names the way systemctl does: append ".service" if no suffix is present.</summary>
public static class UnitName
{
    public const string DefaultSuffix = ".service";

    public static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Contains('.') ? name : name + DefaultSuffix;
    }
}
