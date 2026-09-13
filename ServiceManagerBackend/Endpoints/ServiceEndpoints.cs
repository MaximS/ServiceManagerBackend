using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;
using Microsoft.Extensions.Options;
using ServiceManagerBackend.DBus;
using ServiceManagerBackend.Options;
using Tmds.DBus.Protocol;

namespace ServiceManagerBackend;

/// <summary>Maps the /api/services route group. All routes are minimal APIs.</summary>
public static class ServiceEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapAll(this WebApplication app)
    {
        // Literal "start-all"/"stop-all" routes are registered before the "{name}"
        // routes so the router never mistakes them for service names.
        IEndpointRouteBuilder group = app.MapGroup("/api/services").WithTags("Services");
        group.MapStartAll();
        group.MapStopAll();
        group.MapList();
        group.MapGetStatus();
        group.MapStart();
        group.MapStop();
        group.MapMonitorStart();
        group.MapMonitorStop();
    }

    private static void MapList(this IEndpointRouteBuilder group)
    {
        group.MapGet("/", async (SystemdClient client, ServiceWhitelist whitelist, HttpContext ctx) =>
        {
            await client.EnsureConnectedAsync(ctx.RequestAborted).ConfigureAwait(false);

            // One GetAll round trip per whitelisted service; they are independent, so run them in parallel.
            var statuses = await Task.WhenAll(whitelist.All.Select(async name =>
                await client.GetUnitStatusAsync(name, ctx.RequestAborted).ConfigureAwait(false))).ConfigureAwait(false);

            return Results.Ok(statuses.Select(MapStatusDto));
        })
        .WithName("ListServices")
        .AddOpenApiOperationTransformer((op, _, _) =>
{
    op.Summary = "Get the state of every whitelisted service";
    return Task.CompletedTask;
});
    }

    private static void MapGetStatus(this IEndpointRouteBuilder group)
    {
        group.MapGet("/{name}", async (string name, SystemdClient client, ServiceWhitelist whitelist, HttpContext ctx) =>
        {
            if (!whitelist.IsAllowed(name))
            {
                return Results.NotFound(new ErrorDto("ServiceNotWhitelisted", null, $"'{name}' is not in the configured service list."));
            }

            await client.EnsureConnectedAsync(ctx.RequestAborted).ConfigureAwait(false);

            try
            {
                UnitStatus status = await client.GetUnitStatusAsync(name, ctx.RequestAborted).ConfigureAwait(false);
                if (status.LoadState is "not-found" or "not-loaded")
                {
                    return Results.NotFound(new ErrorDto("UnitNotFound", null, $"No unit file found for '{UnitName.Normalize(name)}'."));
                }

                return Results.Ok(MapStatusDto(status));
            }
            catch (DBusErrorReplyException ex)
            {
                return DbusErrorResult(ex);
            }
        })
        .WithName("GetService")
        .AddOpenApiOperationTransformer((op, _, _) =>
{
    op.Summary = "Get the state of a single service";
    return Task.CompletedTask;
});
    }

    private static void MapStart(this IEndpointRouteBuilder group)
    {
        group.MapPost("/{name}/start", async (string name, SystemdClient client, ServiceWhitelist whitelist, IOptions<ServicesOptions> options, HttpContext ctx) =>
        {
            if (!whitelist.IsAllowed(name))
            {
                return Results.NotFound(new ErrorDto("ServiceNotWhitelisted", null, $"'{name}' is not in the configured service list."));
            }

            return await ControlOneAsync(client, name, UnitJob.Start, options.Value, ctx).ConfigureAwait(false);
        })
        .WithName("StartService")
        .AddOpenApiOperationTransformer((op, _, _) =>
{
    op.Summary = "Start a service and wait for the job to finish";
    return Task.CompletedTask;
});
    }

    private static void MapStop(this IEndpointRouteBuilder group)
    {
        group.MapPost("/{name}/stop", async (string name, SystemdClient client, ServiceWhitelist whitelist, IOptions<ServicesOptions> options, HttpContext ctx) =>
        {
            if (!whitelist.IsAllowed(name))
            {
                return Results.NotFound(new ErrorDto("ServiceNotWhitelisted", null, $"'{name}' is not in the configured service list."));
            }

            return await ControlOneAsync(client, name, UnitJob.Stop, options.Value, ctx).ConfigureAwait(false);
        })
        .WithName("StopService")
        .AddOpenApiOperationTransformer((op, _, _) =>
{
    op.Summary = "Stop a service and wait for the job to finish";
    return Task.CompletedTask;
});
    }

    /// <summary>Shared start/stop body: enqueue a job, wait for JobRemoved, map the outcome to a status code.</summary>
    private static async Task<IResult> ControlOneAsync(
        SystemdClient client,
        string name,
        UnitJob job,
        ServicesOptions options,
        HttpContext ctx)
    {
        await client.EnsureConnectedAsync(ctx.RequestAborted).ConfigureAwait(false);

        try
        {
            JobOutcome outcome = await client
                .EnqueueUnitJobAsync(name, job, waitForCompletion: true,
                    waitTimeout: TimeSpan.FromSeconds(options.JobTimeoutSeconds), ctx.RequestAborted)
                .ConfigureAwait(false);

            if (outcome.Result == SystemdClient.JobResults.Done)
            {
                return Results.Ok(new JobResultDto(outcome.JobId, outcome.JobPath.ToString(), outcome.Result));
            }

            // The job ran but did not succeed (failed / dependency / timeout / ...).
            return Results.Json(
                new ErrorDto("JobFailed", null, $"Job for '{UnitName.Normalize(name)}' finished with result '{outcome.Result}'."),
                statusCode: 500);
        }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            // Not a client disconnect: the per-job wait timeout expired.
            return Results.Json(
                new ErrorDto("JobTimeout", null, $"Timed out waiting for the job for '{UnitName.Normalize(name)}'."),
                statusCode: 504);
        }
        catch (DBusErrorReplyException ex)
        {
            return DbusErrorResult(ex);
        }
    }

    private static void MapStartAll(this IEndpointRouteBuilder group)
    {
        group.MapPost("/start-all", async (SystemdClient client, IOptions<ServicesOptions> options, HttpContext ctx) =>
        {
            return await RunSequenceAsync(client, options.Value, UnitJob.Start, ctx).ConfigureAwait(false);
        })
        .WithName("StartAll")
        .AddOpenApiOperationTransformer((op, _, _) =>
{
    op.Summary = "Start all services in the configured StartAll sequence (waits for each, fail-fast)";
    return Task.CompletedTask;
});
    }

    private static void MapStopAll(this IEndpointRouteBuilder group)
    {
        group.MapPost("/stop-all", async (SystemdClient client, IOptions<ServicesOptions> options, HttpContext ctx) =>
        {
            return await RunSequenceAsync(client, options.Value, UnitJob.Stop, ctx).ConfigureAwait(false);
        })
        .WithName("StopAll")
        .AddOpenApiOperationTransformer((op, _, _) =>
{
    op.Summary = "Stop all services in the configured StopAll sequence (waits for each, fail-fast)";
    return Task.CompletedTask;
});
    }

    /// <summary>
    /// start-all / stop-all: walk the configured sequence in order, waiting for each job
    /// to finish before starting the next. The first failing job aborts the sequence (fail-fast).
    /// </summary>
    private static async Task<IResult> RunSequenceAsync(
        SystemdClient client,
        ServicesOptions options,
        UnitJob job,
        HttpContext ctx)
    {
        string[] sequence = job == UnitJob.Start ? options.Sequences.StartAll : options.Sequences.StopAll;
        await client.EnsureConnectedAsync(ctx.RequestAborted).ConfigureAwait(false);

        var completed = new List<string>(sequence.Length);

        foreach (string service in sequence)
        {
            string name = UnitName.Normalize(service);

            try
            {
                JobOutcome outcome = await client
                    .EnqueueUnitJobAsync(service, job, waitForCompletion: true,
                        waitTimeout: TimeSpan.FromSeconds(options.JobTimeoutSeconds), ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (outcome.Result != SystemdClient.JobResults.Done)
                {
                    return Results.Json(new SequenceFailureDto(name, outcome.Result, completed.ToArray()), statusCode: 500);
                }

                completed.Add(name);
            }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new SequenceFailureDto(name, "timeout", completed.ToArray()), statusCode: 504);
            }
            catch (DBusErrorReplyException ex)
            {
                // Fail-fast: report which service broke the sequence and the D-Bus error name.
                return Results.Json(new SequenceFailureDto(name, ex.ErrorName, completed.ToArray()), statusCode: 500);
            }
        }

        return Results.Ok(new SequenceSuccessDto(completed.ToArray()));
    }

    /// <summary>
    /// monitor-start / monitor-stop: enqueue the job without waiting and stream the progress
    /// back as Server-Sent Events:
    ///   event: job-enqueued   - once, after the job is accepted by systemd
    ///   event: state-changed  - once per unit PropertiesChanged signal while the job runs
    ///   event: job-completed  - once, with the final job result; the stream then closes
    ///   event: error          - on D-Bus errors or wait timeout; the stream then closes
    /// Note: systemd can emit the first PropertiesChanged (job assignment) before it
    /// sends the Start/Stop method reply, so a state-changed frame may legitimately
    /// precede job-enqueued. job-completed is always the last frame.
    /// </summary>
    private static void MapMonitorStart(this IEndpointRouteBuilder group)
    {
        group.MapPost("/{name}/monitor-start", (string name, SystemdClient client, ServiceWhitelist whitelist, IOptions<ServicesOptions> options, HttpContext ctx) =>
        {
            if (!whitelist.IsAllowed(name))
            {
                return Results.NotFound(new ErrorDto("ServiceNotWhitelisted", null, $"'{name}' is not in the configured service list."));
            }

            // Results.Stream writes each chunk as it becomes available and flushes: true SSE streaming.
            return Results.Stream(async stream =>
            {
                await foreach (byte[] frame in StreamJobAsync(client, name, UnitJob.Start, options.Value, ctx).ConfigureAwait(false))
                {
                    await stream.WriteAsync(frame, ctx.RequestAborted).ConfigureAwait(false);
                    await stream.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                }
            }, "text/event-stream");
        })
        .WithName("MonitorStart")
        .DisableAntiforgery()
        .AddOpenApiOperationTransformer((op, _, _) =>
        {
            op.Summary = "Start a service and stream job progress as Server-Sent Events";
            op.Responses ??= new OpenApiResponses();
            op.Responses["200"] = new OpenApiResponse { Description = "SSE stream: job-enqueued, state-changed*, job-completed | error" };
            return Task.CompletedTask;
        });
    }

    private static void MapMonitorStop(this IEndpointRouteBuilder group)
    {
        group.MapPost("/{name}/monitor-stop", (string name, SystemdClient client, ServiceWhitelist whitelist, IOptions<ServicesOptions> options, HttpContext ctx) =>
        {
            if (!whitelist.IsAllowed(name))
            {
                return Results.NotFound(new ErrorDto("ServiceNotWhitelisted", null, $"'{name}' is not in the configured service list."));
            }

            return Results.Stream(async stream =>
            {
                await foreach (byte[] frame in StreamJobAsync(client, name, UnitJob.Stop, options.Value, ctx).ConfigureAwait(false))
                {
                    await stream.WriteAsync(frame, ctx.RequestAborted).ConfigureAwait(false);
                    await stream.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                }
            }, "text/event-stream");
        })
        .WithName("MonitorStop")
        .DisableAntiforgery()
        .AddOpenApiOperationTransformer((op, _, _) =>
        {
            op.Summary = "Stop a service and stream job progress as Server-Sent Events";
            op.Responses ??= new OpenApiResponses();
            op.Responses["200"] = new OpenApiResponse { Description = "SSE stream: job-enqueued, state-changed*, job-completed | error" };
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The SSE producer: installs the JobRemoved and PropertiesChanged watchers BEFORE
    /// enqueuing the job (race-free, same pattern as the waiting path), then pumps events
    /// through an unbounded channel until the job completes, the timeout fires, or the
    /// client disconnects.
    /// </summary>
    private static async IAsyncEnumerable<byte[]> StreamJobAsync(
        SystemdClient client,
        string name,
        UnitJob job,
        ServicesOptions options,
        HttpContext ctx,
        [EnumeratorCancellation] CancellationToken _ = default)
    {
        string unit = UnitName.Normalize(name);

        // The stream ends when the client goes away OR the job wait timeout expires, whichever first.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(options.JobTimeoutSeconds));
        CancellationToken ct = timeoutSource.Token;

        var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false, // writers are the D-Bus signal handlers plus this producer
        });

        var jobCompleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await client.EnsureConnectedAsync(ctx.RequestAborted).ConfigureAwait(false);

        // Writers 1+2: D-Bus signal handlers (may run on D-Bus worker threads).
        using (IDisposable stateWatch = await client.WatchUnitStateAsync(
                   unit,
                   change => channel.Writer.TryWrite(SseFrame(
                       "state-changed",
                       new StateChangedDto(
                           change.LoadState, change.ActiveState, change.SubState, change.UnitFileState,
                           change.Job?.Id))),
                   ctx.RequestAborted).ConfigureAwait(false))
        using (IDisposable jobWatch = await client.WatchJobRemovedAsync(
                   unit,
                   args => jobCompleted.TrySetResult(args.Result),
                   ctx.RequestAborted).ConfigureAwait(false))
        {
            // Enqueue AFTER the watchers are in place so a fast job cannot emit
            // JobRemoved before we are listening.
            JobOutcome enqueued;
            try
            {
                enqueued = await client.EnqueueUnitJobAsync(unit, job, waitForCompletion: false, cancellationToken: ctx.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (DBusErrorReplyException ex)
            {
                channel.Writer.TryWrite(SseFrame("error", new ErrorEventDto(DbusMessage(ex, unit))));
                channel.Writer.TryComplete();
                yield break;
            }

            channel.Writer.TryWrite(SseFrame("job-enqueued", new JobEnqueuedDto(unit, enqueued.JobId, enqueued.JobPath.ToString())));

            // Wait for the job to finish, the timeout, or client disconnect.
            try
            {
                string result = await jobCompleted.Task.WaitAsync(ct).ConfigureAwait(false);
                channel.Writer.TryWrite(SseFrame("job-completed", new JobCompletedDto(enqueued.JobId, result)));
            }
            catch (OperationCanceledException)
            {
                if (!ctx.RequestAborted.IsCancellationRequested)
                {
                    channel.Writer.TryWrite(SseFrame("error", new ErrorEventDto($"Timed out after {options.JobTimeoutSeconds}s waiting for the job for '{unit}'.")));
                }
            }

            channel.Writer.TryComplete();
        }

        await foreach (byte[] frame in channel.Reader.ReadAllAsync(ctx.RequestAborted).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    /// <summary>Formats one Server-Sent Event frame.</summary>
    private static byte[] SseFrame(string eventName, object payload)
    {
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        return Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");
    }

    /// <summary>Maps a unit status record to the API DTO.</summary>
    private static ServiceStatusDto MapStatusDto(UnitStatus status) => new(
        Name: status.Id,
        Description: status.Description,
        LoadState: status.LoadState,
        ActiveState: status.ActiveState,
        SubState: status.SubState,
        UnitFileState: status.UnitFileState,
        FragmentPath: status.FragmentPath,
        JobId: status.Job?.Id,
        JobPath: status.Job?.Path.ToString(),
        StateChangedUtc: status.StateChangeTimestampMicroseconds > 0
            ? DateTimeOffset.UnixEpoch.AddMicroseconds((long)status.StateChangeTimestampMicroseconds).UtcDateTime.ToString("O")
            : null);

    /// <summary>Maps systemd D-Bus error names to HTTP status codes and friendly messages.</summary>
    private static IResult DbusErrorResult(DBusErrorReplyException ex) => ex.ErrorName switch
    {
        "org.freedesktop.systemd1.UnitNotFound" =>
            Results.Json(new ErrorDto("UnitNotFound", ex.ErrorName, ex.ErrorMessage), statusCode: 404),
        "org.freedesktop.systemd1.UnitMasked" =>
            Results.Json(new ErrorDto("UnitMasked", ex.ErrorName, ex.ErrorMessage), statusCode: 409),
        "org.freedesktop.systemd1.UnitNotActive" =>
            Results.Json(new ErrorDto("UnitNotActive", ex.ErrorName, ex.ErrorMessage), statusCode: 409),
        // A unit the job depends on (Requires/Wants) does not exist.
        "org.freedesktop.systemd1.NoSuchUnit" =>
            Results.Json(new ErrorDto("NoSuchUnit", ex.ErrorName, ex.ErrorMessage), statusCode: 502),
        "org.freedesktop.DBus.Error.AccessDenied"
            or "org.freedesktop.DBus.Error.InteractiveAuthorizationRequired"
            or "org.freedesktop.PolicyKit1.Error.Failed" =>
            // Should not happen on the session bus (owner manages own units), but keep the mapping for clarity.
            Results.Json(new ErrorDto("AccessDenied", ex.ErrorName, ex.ErrorMessage), statusCode: 403),
        "org.freedesktop.DBus.Error.NoReply" =>
            Results.Json(new ErrorDto("NoReply", ex.ErrorName, ex.ErrorMessage), statusCode: 504),
        _ => Results.Json(new ErrorDto("DBusError", ex.ErrorName, ex.ErrorMessage), statusCode: 500),
    };

    private static string DbusMessage(DBusErrorReplyException ex, string unit)
    {
        switch (ex.ErrorName)
        {
            case "org.freedesktop.systemd1.UnitNotFound":
                return $"Unit not found for '{unit}'.";
            case "org.freedesktop.systemd1.UnitMasked":
                return $"Unit '{unit}' is masked.";
            case "org.freedesktop.systemd1.UnitNotActive":
                return $"Unit '{unit}' is not active.";
            default:
                return $"D-Bus error {ex.ErrorName}: {ex.ErrorMessage}";
        }
    }
}
