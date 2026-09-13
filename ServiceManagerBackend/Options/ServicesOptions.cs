namespace ServiceManagerBackend.Options;

/// <summary>
/// Bound from the "ServiceManager" section of appsettings.json.
/// Service names are given WITHOUT the ".service" suffix (normalized at runtime).
/// </summary>
public sealed class ServicesOptions
{
    public const string SectionName = "ServiceManager";

    /// <summary>Whitelist of services the API may manage. Everything else gets 404.</summary>
    public required string[] Services { get; init; }

    /// <summary>Exact order in which "start all" / "stop all" operate. Each step waits for the previous one to finish.</summary>
    public required SequencesOptions Sequences { get; init; }

    /// <summary>Per-job wait timeout for the waiting endpoints and for start-all/stop-all.</summary>
    public int JobTimeoutSeconds { get; init; } = 600;
}

public sealed class SequencesOptions
{
    public required string[] StartAll { get; init; }
    public required string[] StopAll { get; init; }
}
