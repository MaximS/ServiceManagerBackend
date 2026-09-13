namespace ServiceManagerBackend;

/// <summary>Request/response DTOs shared by the endpoints.</summary>
public sealed record ServiceStatusDto(
    string Name,
    string Description,
    string LoadState,
    string ActiveState,
    string SubState,
    string UnitFileState,
    string FragmentPath,
    uint? JobId,
    string? JobPath,
    string? StateChangedUtc);

public sealed record JobResultDto(uint JobId, string JobPath, string Result);

public sealed record SequenceSuccessDto(string[] Completed);

public sealed record SequenceFailureDto(string FailedService, string? Result, string[] Completed);

public sealed record ErrorDto(string Error, string? ErrorName, string Message);

// SSE payload shapes (one JSON "data" object per event).

public sealed record JobEnqueuedDto(string Unit, uint JobId, string JobPath);

public sealed record StateChangedDto(
    string? LoadState,
    string? ActiveState,
    string? SubState,
    string? UnitFileState,
    uint? JobId);

public sealed record JobCompletedDto(uint JobId, string Result);

public sealed record ErrorEventDto(string Message);
