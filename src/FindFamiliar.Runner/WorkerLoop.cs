using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FindFamiliar.Runner;

public enum WorkerPollOutcome
{
    /// <summary>Work was claimed and executed. Poll again immediately.</summary>
    Executed,

    /// <summary>Nothing eligible right now. Back off.</summary>
    Idle,

    /// <summary>The server rejected this worker (unknown, disabled) or the request was refused.</summary>
    Rejected,

    /// <summary>Transport or protocol failure. Back off and retry.</summary>
    Failed
}

/// <summary>
/// The polling worker (ADR-0008): identify, heartbeat, ask for one claim, execute it through the
/// existing <see cref="RunnerEngine"/>, back off when idle, and stop cleanly on cancellation.
///
/// The loop makes no workflow decisions. It never selects work, never starts sessions, never
/// chooses a role, and never decides task completion — it asks the server for one claim at a time
/// and executes exactly what it is given.
/// </summary>
public sealed class WorkerLoop(
    HttpClient httpClient,
    RunnerEngine engine,
    WorkerConfiguration configuration,
    TextWriter diagnostics,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private DateTimeOffset _nextHeartbeatUtc = DateTimeOffset.MinValue;
    private Guid? _workerId;
    private bool _workerEnabled;

    public async Task<RunnerExitCode> RunAsync(CancellationToken cancellationToken)
    {
        diagnostics.WriteLine(
            $"worker: starting (key={configuration.WorkerKey}, capabilities={string.Join(",", configuration.Capabilities)}, " +
            $"projects={configuration.Projects.Count}, poll={configuration.PollInterval.TotalSeconds:0}s).");

        var backoff = configuration.PollInterval;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var outcome = await PollOnceAsync(cancellationToken);

                if (outcome == WorkerPollOutcome.Executed)
                {
                    // Work was available, so more may be queued: reset to the fastest cadence and
                    // poll again without delay.
                    backoff = configuration.PollInterval;
                    continue;
                }

                // Every non-executing outcome waits before retrying, then widens the interval up
                // to the configured ceiling. There is no path back around this loop that skips the
                // delay, so an idle or persistently failing worker can never busy-loop.
                await DelayWithHeartbeatsAsync(backoff, cancellationToken);
                backoff = NextBackoff(backoff);
            }
        }
        catch (OperationCanceledException)
        {
            // Requested shutdown, not a failure.
        }

        diagnostics.WriteLine("worker: stopped cleanly.");
        return RunnerExitCode.Success;
    }

    /// <summary>
    /// One heartbeat-and-claim cycle. Exposed so tests can drive the loop deterministically
    /// without waiting on real backoff delays.
    /// </summary>
    public async Task<WorkerPollOutcome> PollOnceAsync(CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        if (nowUtc >= _nextHeartbeatUtc)
        {
            if (!await SendHeartbeatAsync(cancellationToken))
            {
                ScheduleHeartbeatRetry();
                return WorkerPollOutcome.Failed;
            }

            _nextHeartbeatUtc = timeProvider.GetUtcNow() + configuration.HeartbeatInterval;
        }

        if (!_workerEnabled)
        {
            diagnostics.WriteLine("worker: this worker is disabled; not requesting a claim.");
            return WorkerPollOutcome.Rejected;
        }

        var (claim, outcome) = await RequestClaimAsync(cancellationToken);
        if (claim is null)
        {
            return outcome;
        }

        var mapping = configuration.FindProject(claim.ProjectId);
        if (mapping is null)
        {
            // The server only offers projects this worker asked for, so this means the local
            // mapping changed mid-flight. Leave the claim to expire rather than executing work
            // with no repository, and never guess a path.
            diagnostics.WriteLine("worker: claimed work has no local repository mapping; leaving the lease to expire.");
            return WorkerPollOutcome.Failed;
        }

        diagnostics.WriteLine(
            $"worker: claimed session (task={claim.TaskId}, session={claim.SessionId}, role={claim.Role}). Executing.");

        var exitCode = await ExecuteWithMaintenanceAsync(
            claim,
            new RunnerExecutionRequest(
                claim.TaskId,
                claim.SessionId,
                configuration.FamiliarToken,
                configuration.AdapterPath,
                configuration.AdapterArguments,
                configuration.AdapterTimeout,
                claim.RolePrompt,
                claim.AssignmentMarkdown,
                claim.Role,
                mapping.ToAdapterEnvironment(claim.Role),
                claim.ClaimId,
                mapping.ToWorkspaceContract(claim.Role)),
            cancellationToken);

        if (exitCode is null)
        {
            return WorkerPollOutcome.Failed;
        }

        diagnostics.WriteLine($"worker: execution finished with exit code {(int)exitCode.Value} ({exitCode.Value}).");

        return WorkerPollOutcome.Executed;
    }

    private TimeSpan NextBackoff(TimeSpan current)
    {
        var doubled = TimeSpan.FromTicks(current.Ticks * 2);
        return doubled > configuration.MaxPollInterval ? configuration.MaxPollInterval : doubled;
    }

    private async Task DelayWithHeartbeatsAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var deadlineUtc = timeProvider.GetUtcNow() + delay;

        while (timeProvider.GetUtcNow() < deadlineUtc)
        {
            var wakeUtc = _nextHeartbeatUtc < deadlineUtc ? _nextHeartbeatUtc : deadlineUtc;
            var remaining = wakeUtc - timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, timeProvider, cancellationToken);
            }

            if (timeProvider.GetUtcNow() >= _nextHeartbeatUtc)
            {
                if (await SendHeartbeatAsync(cancellationToken))
                {
                    _nextHeartbeatUtc = timeProvider.GetUtcNow() + configuration.HeartbeatInterval;
                }
                else
                {
                    ScheduleHeartbeatRetry();
                }
            }
        }
    }

    private async Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildRequest(
                "api/runner/workers/heartbeat",
                new WorkerHeartbeatRequestBody(
                    RunnerProtocol.ContractVersion,
                    configuration.WorkerKey,
                    configuration.DisplayName,
                    configuration.Capabilities));

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                diagnostics.WriteLine($"worker: heartbeat returned status {(int)response.StatusCode}.");
                return false;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var heartbeat = await JsonSerializer.DeserializeAsync<WorkerHeartbeatResponse>(
                body,
                JsonOptions,
                cancellationToken);

            if (heartbeat is null
                || heartbeat.ContractVersion != RunnerProtocol.ContractVersion
                || heartbeat.WorkerId == Guid.Empty
                || (_workerId is not null && _workerId != heartbeat.WorkerId))
            {
                diagnostics.WriteLine("worker: heartbeat failed contract/identity validation.");
                return false;
            }

            _workerId = heartbeat.WorkerId;
            _workerEnabled = heartbeat.Enabled;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            diagnostics.WriteLine("worker: heartbeat failed (transport error).");
            return false;
        }
    }

    private void ScheduleHeartbeatRetry()
    {
        var retry = configuration.PollInterval < configuration.HeartbeatInterval
            ? configuration.PollInterval
            : configuration.HeartbeatInterval;
        _nextHeartbeatUtc = timeProvider.GetUtcNow() + retry;
    }

    private async Task<(WorkerClaimResponse? Claim, WorkerPollOutcome Outcome)> RequestClaimAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildRequest(
                "api/runner/workers/claim",
                new WorkerClaimRequestBody(
                    RunnerProtocol.ContractVersion,
                    configuration.WorkerKey,
                    configuration.ProjectIds,
                    configuration.LeaseSeconds));

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return (null, WorkerPollOutcome.Idle);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Unauthorized)
            {
                diagnostics.WriteLine(
                    $"worker: claim refused with status {(int)response.StatusCode} " +
                    "(unknown worker, disabled worker, or bad credential).");
                return (null, WorkerPollOutcome.Rejected);
            }

            if (!response.IsSuccessStatusCode)
            {
                diagnostics.WriteLine($"worker: claim returned status {(int)response.StatusCode}.");
                return (null, WorkerPollOutcome.Failed);
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var claim = await JsonSerializer.DeserializeAsync<WorkerClaimResponse>(body, JsonOptions, cancellationToken);

            if (claim is null || !IsClaimValid(claim))
            {
                diagnostics.WriteLine("worker: claim failed contract validation.");
                return (null, WorkerPollOutcome.Failed);
            }

            return (claim, WorkerPollOutcome.Executed);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            diagnostics.WriteLine("worker: claim request failed (transport or parse error).");
            return (null, WorkerPollOutcome.Failed);
        }
    }

    private bool IsClaimValid(WorkerClaimResponse claim) =>
        claim.ContractVersion == RunnerProtocol.ContractVersion
        && _workerId == claim.WorkerId
        && claim.ClaimId != Guid.Empty
        && claim.TaskId != Guid.Empty
        && claim.SessionId != Guid.Empty
        && claim.ProjectId != Guid.Empty
        && !string.IsNullOrWhiteSpace(claim.Role)
        && !string.IsNullOrWhiteSpace(claim.RolePrompt)
        && !string.IsNullOrWhiteSpace(claim.AssignmentMarkdown)
        && claim.AssignmentMarkdown.Length <= RunnerProtocol.MaxAssignmentMarkdownLength
        && claim.LeaseExpiresUtc > timeProvider.GetUtcNow().UtcDateTime.AddSeconds(
            Math.Min(5, Math.Max(1, configuration.LeaseSeconds / 10)));

    private async Task<RunnerExitCode?> ExecuteWithMaintenanceAsync(
        WorkerClaimResponse claim,
        RunnerExecutionRequest request,
        CancellationToken cancellationToken)
    {
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var executionTask = engine.ExecuteAssignmentAsync(request, executionCts.Token);
        var leaseExpiresUtc = new DateTimeOffset(claim.LeaseExpiresUtc, TimeSpan.Zero);
        var nextRenewalUtc = NextRenewalUtc(timeProvider.GetUtcNow(), leaseExpiresUtc);

        try
        {
            while (!executionTask.IsCompleted)
            {
                var wakeUtc = _nextHeartbeatUtc < nextRenewalUtc ? _nextHeartbeatUtc : nextRenewalUtc;
                var remaining = wakeUtc - timeProvider.GetUtcNow();
                var maintenanceDelay = remaining > TimeSpan.Zero
                    ? Task.Delay(remaining, timeProvider, cancellationToken)
                    : Task.CompletedTask;

                if (await Task.WhenAny(executionTask, maintenanceDelay) == executionTask)
                {
                    return await executionTask;
                }

                await maintenanceDelay;
                var nowUtc = timeProvider.GetUtcNow();

                if (nowUtc >= _nextHeartbeatUtc)
                {
                    if (!await SendHeartbeatAsync(cancellationToken) || !_workerEnabled)
                    {
                        diagnostics.WriteLine("worker: heartbeat failed or worker was disabled during execution; stopping adapter.");
                        return await StopStaleExecutionAsync(executionTask, executionCts);
                    }

                    _nextHeartbeatUtc = timeProvider.GetUtcNow() + configuration.HeartbeatInterval;
                }

                if (nowUtc >= nextRenewalUtc)
                {
                    var renewedUntil = await RenewClaimAsync(claim, cancellationToken);
                    if (renewedUntil is null)
                    {
                        diagnostics.WriteLine("worker: claim renewal failed; stopping adapter before this claim becomes stale.");
                        return await StopStaleExecutionAsync(executionTask, executionCts);
                    }

                    leaseExpiresUtc = renewedUntil.Value;
                    nextRenewalUtc = NextRenewalUtc(timeProvider.GetUtcNow(), leaseExpiresUtc);
                }
            }

            return await executionTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            executionCts.Cancel();
            await ObserveCancelledExecutionAsync(executionTask);
            throw;
        }
    }

    private async Task<DateTimeOffset?> RenewClaimAsync(
        WorkerClaimResponse claim,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildRequest(
                "api/runner/workers/claims/renew",
                new WorkerClaimRenewRequestBody(
                    RunnerProtocol.ContractVersion,
                    configuration.WorkerKey,
                    claim.SessionId,
                    claim.ClaimId,
                    configuration.LeaseSeconds));

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                diagnostics.WriteLine($"worker: claim renewal returned status {(int)response.StatusCode}.");
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var renewal = await JsonSerializer.DeserializeAsync<WorkerClaimRenewResponse>(
                body,
                JsonOptions,
                cancellationToken);

            if (renewal is null
                || renewal.ContractVersion != RunnerProtocol.ContractVersion
                || renewal.SessionId != claim.SessionId
                || renewal.ClaimId != claim.ClaimId
                || renewal.LeaseExpiresUtc <= timeProvider.GetUtcNow().UtcDateTime)
            {
                diagnostics.WriteLine("worker: claim renewal failed contract/identity validation.");
                return null;
            }

            return new DateTimeOffset(renewal.LeaseExpiresUtc, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            diagnostics.WriteLine("worker: claim renewal failed (transport or parse error).");
            return null;
        }
    }

    private static DateTimeOffset NextRenewalUtc(DateTimeOffset nowUtc, DateTimeOffset leaseExpiresUtc)
    {
        var remaining = leaseExpiresUtc - nowUtc;
        return remaining <= TimeSpan.Zero ? nowUtc : nowUtc + TimeSpan.FromTicks(remaining.Ticks / 3);
    }

    private static async Task<RunnerExitCode?> StopStaleExecutionAsync(
        Task<RunnerExitCode> executionTask,
        CancellationTokenSource executionCts)
    {
        executionCts.Cancel();
        await ObserveCancelledExecutionAsync(executionTask);
        return null;
    }

    private static async Task ObserveCancelledExecutionAsync(Task<RunnerExitCode> executionTask)
    {
        try
        {
            await executionTask;
        }
        catch (OperationCanceledException)
        {
            // Expected after a lease/heartbeat loss or requested service shutdown.
        }
    }

    private HttpRequestMessage BuildRequest<TBody>(string route, TBody body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.FamiliarToken);
        return request;
    }
}
