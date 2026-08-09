using System.Text.Json;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Api.Runner;

/// <summary>
/// Authenticated machine endpoints for the provider-neutral runner bridge (ADR-0006). All three
/// routes sit behind <see cref="RunnerBridgeAuthenticationFilter"/>, applied once to the route
/// group so authentication always runs before any task/session lookup.
/// </summary>
public static class RunnerEndpoints
{
    public static void MapRunnerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner").AddEndpointFilter<RunnerBridgeAuthenticationFilter>();

        group.MapGet("/tasks/{taskId:guid}/sessions/{sessionId:guid}/assignment", GetAssignmentAsync);
        group.MapPost("/tasks/{taskId:guid}/sessions/{sessionId:guid}/result", PostResultAsync);
        group.MapPost("/tasks/{taskId:guid}/sessions/{sessionId:guid}/cancel", PostCancelAsync);
        group.MapPost("/workers/heartbeat", PostWorkerHeartbeatAsync);
        group.MapPost("/workers/claim", PostWorkerClaimAsync);
        group.MapPost("/workers/claims/renew", PostWorkerClaimRenewAsync);
    }

    private static async Task<IResult> PostWorkerHeartbeatAsync(
        HttpRequest httpRequest,
        IWorkerCoordinationService workerCoordination,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        var (payload, error) = await ReadBodyAsync<WorkerHeartbeatRequestBody>(httpRequest, jsonOptions.Value, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var outcome = await workerCoordination.HeartbeatAsync(
            new WorkerHeartbeatRequest(payload!.WorkerKey, payload.DisplayName, payload.Capabilities),
            cancellationToken);

        return outcome.Status switch
        {
            WorkerHeartbeatStatus.Success => Results.Json(new WorkerHeartbeatResponse(
                RunnerContracts.ContractVersion,
                outcome.WorkerId,
                outcome.Enabled,
                outcome.Availability)),
            WorkerHeartbeatStatus.ValidationFailed => Results.BadRequest(
                RunnerErrorResponse.Create("Validation failed.", outcome.ValidationErrors)),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>
    /// Grants at most one claim and returns its assignment in the same response. There is no
    /// separate "list eligible work" route: listing and claiming as two calls is exactly the race
    /// this single atomic operation exists to avoid (ADR-0008).
    /// </summary>
    private static async Task<IResult> PostWorkerClaimAsync(
        HttpRequest httpRequest,
        IWorkerCoordinationService workerCoordination,
        IContextProjectionService contextProjection,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        var (payload, error) = await ReadBodyAsync<WorkerClaimRequestBody>(httpRequest, jsonOptions.Value, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var outcome = await workerCoordination.ClaimNextAsync(
            new WorkerClaimRequest(payload!.WorkerKey, payload.ProjectIds, payload.LeaseSeconds),
            cancellationToken);

        switch (outcome.Status)
        {
            case WorkerClaimStatus.NoWorkAvailable:
                return Results.NoContent();
            case WorkerClaimStatus.UnknownWorker:
                return Results.NotFound(RunnerErrorResponse.Create(
                    "Unknown worker. Send a heartbeat to register before requesting work."));
            case WorkerClaimStatus.WorkerDisabled:
                return Results.Conflict(RunnerErrorResponse.Create("This worker is disabled."));
            case WorkerClaimStatus.ValidationFailed:
                return Results.BadRequest(RunnerErrorResponse.Create("Validation failed.", outcome.ValidationErrors));
        }

        var claim = outcome.Claim!;
        var document = await contextProjection.GetTaskContextAsync(claim.TaskId, cancellationToken);
        var session = document?.Sessions.SingleOrDefault(candidate => candidate.Id == claim.SessionId);

        if (document is null
            || session is null
            || session.Status != AgentSessionStatus.Started
            || session.Role != claim.Role
            || session.ContextRevisionRead != claim.ContextRevisionRead)
        {
            // The task disappeared between the claim and this read. Release rather than hold a
            // lease on work this server cannot describe.
            await workerCoordination.ReleaseClaimAsync(claim.SessionId, claim.WorkerId, claim.ClaimId, cancellationToken);
            return Results.NoContent();
        }

        var rolePrompt = SessionAssignmentMarkdownRenderer.RenderRolePrompt(session.Role, document);
        var assignmentMarkdown = SessionAssignmentMarkdownRenderer.RenderAssignment(document, session);

        if (assignmentMarkdown.Length > RunnerContracts.MaxAssignmentMarkdownLength)
        {
            // Holding a lease on work no worker can be handed would block the session until the
            // lease expired, so the claim is given straight back.
            await workerCoordination.ReleaseClaimAsync(claim.SessionId, claim.WorkerId, claim.ClaimId, cancellationToken);
            return Results.Json(
                RunnerErrorResponse.Create("The assignment exceeds the runner contract's size limit."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        return Results.Json(new WorkerClaimResponse(
            RunnerContracts.ContractVersion,
            claim.WorkerId,
            claim.ClaimId,
            claim.ProjectId,
            claim.TaskId,
            claim.SessionId,
            session.Role,
            session.ContextRevisionRead,
            rolePrompt,
            assignmentMarkdown,
            claim.ClaimedUtc,
            claim.LeaseExpiresUtc));
    }

    private static async Task<IResult> PostWorkerClaimRenewAsync(
        HttpRequest httpRequest,
        IWorkerCoordinationService workerCoordination,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        var (payload, error) = await ReadBodyAsync<WorkerClaimRenewRequestBody>(
            httpRequest,
            jsonOptions.Value,
            cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var outcome = await workerCoordination.RenewClaimAsync(
            new WorkerClaimRenewalRequest(
                payload!.WorkerKey,
                payload.SessionId,
                payload.ClaimId,
                payload.LeaseSeconds),
            cancellationToken);

        return outcome.Status switch
        {
            WorkerClaimRenewalStatus.Renewed => Results.Json(new WorkerClaimRenewResponse(
                RunnerContracts.ContractVersion,
                payload.SessionId,
                payload.ClaimId,
                outcome.LeaseExpiresUtc!.Value)),
            WorkerClaimRenewalStatus.UnknownWorker => Results.NotFound(
                RunnerErrorResponse.Create("Unknown worker.")),
            WorkerClaimRenewalStatus.WorkerDisabled => Results.Conflict(
                RunnerErrorResponse.Create("This worker is disabled.")),
            WorkerClaimRenewalStatus.ClaimLost => Results.Conflict(
                RunnerErrorResponse.Create("This claim is no longer active.")),
            WorkerClaimRenewalStatus.ValidationFailed => Results.BadRequest(
                RunnerErrorResponse.Create("Validation failed.", outcome.ValidationErrors)),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<IResult> GetAssignmentAsync(
        Guid taskId,
        Guid sessionId,
        IContextProjectionService contextProjection,
        CancellationToken cancellationToken)
    {
        var document = await contextProjection.GetTaskContextAsync(taskId, cancellationToken);
        var session = document?.Sessions.SingleOrDefault(candidate => candidate.Id == sessionId);

        if (document is null || session is null)
        {
            return Results.NotFound(RunnerErrorResponse.Create("Unknown task or session."));
        }

        if (session.Status != AgentSessionStatus.Started)
        {
            return Results.Conflict(RunnerErrorResponse.Create(
                "This session is no longer Started. An assignment can only be generated for a Started session."));
        }

        var rolePrompt = SessionAssignmentMarkdownRenderer.RenderRolePrompt(session.Role, document);
        var assignmentMarkdown = SessionAssignmentMarkdownRenderer.RenderAssignment(document, session);

        if (assignmentMarkdown.Length > RunnerContracts.MaxAssignmentMarkdownLength)
        {
            return Results.Json(
                RunnerErrorResponse.Create("The assignment exceeds the runner contract's size limit."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        return Results.Json(new RunnerAssignmentResponse(
            RunnerContracts.ContractVersion,
            taskId,
            sessionId,
            session.Role,
            session.ContextRevisionRead,
            rolePrompt,
            assignmentMarkdown));
    }

    private static async Task<IResult> PostResultAsync(
        Guid taskId,
        Guid sessionId,
        HttpRequest httpRequest,
        ISessionResultCaptureService resultCapture,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        var (payload, error) = await ReadBodyAsync<RunnerResultRequest>(httpRequest, jsonOptions.Value, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var outcome = await resultCapture.CaptureAsync(
            new SessionResultCaptureRequest(
                taskId,
                sessionId,
                payload!.Prompt,
                payload.RawOutput,
                payload.Summary,
                payload.ArtifactTitle,
                payload.ArtifactContent,
                payload.ClaimId,
                RequireClaimOwnership: true),
            cancellationToken);

        return outcome.Status switch
        {
            SessionResultCaptureStatus.Success => Results.NoContent(),
            SessionResultCaptureStatus.NotFound => Results.NotFound(RunnerErrorResponse.Create("Unknown task or session.")),
            SessionResultCaptureStatus.NotStarted => Results.Conflict(RunnerErrorResponse.Create(
                "This session is no longer Started. A result can only be captured once for a Started session.")),
            SessionResultCaptureStatus.ClaimLost => Results.Conflict(RunnerErrorResponse.Create(
                "This runner no longer owns the active claim.")),
            SessionResultCaptureStatus.ValidationFailed => Results.BadRequest(
                RunnerErrorResponse.Create("Validation failed.", outcome.ValidationErrors)),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<IResult> PostCancelAsync(
        Guid taskId,
        Guid sessionId,
        HttpRequest httpRequest,
        ISessionCancellationService cancellation,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        var (payload, error) = await ReadBodyAsync<RunnerCancelRequest>(httpRequest, jsonOptions.Value, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var outcome = await cancellation.CancelAsync(
            new SessionCancellationRequest(
                taskId,
                sessionId,
                payload!.Reason,
                payload.ClaimId,
                RequireClaimOwnership: true,
                Diagnostic: payload.Diagnostic is null
                    ? null
                    : new SessionFailureDiagnostic(
                        payload.Diagnostic.Category ?? string.Empty,
                        payload.Diagnostic.AdapterExitCode,
                        payload.Diagnostic.ProviderLaunched,
                        payload.Diagnostic.ProviderExitCode,
                        payload.Diagnostic.Message ?? string.Empty)),
            cancellationToken);

        return outcome.Status switch
        {
            SessionCancellationStatus.Success => Results.NoContent(),
            SessionCancellationStatus.NotFound => Results.NotFound(RunnerErrorResponse.Create("Unknown task or session.")),
            SessionCancellationStatus.NotStarted => Results.Conflict(RunnerErrorResponse.Create(
                "This session is no longer Started. Only a Started session can be cancelled.")),
            SessionCancellationStatus.ClaimLost => Results.Conflict(RunnerErrorResponse.Create(
                "This runner no longer owns the active claim.")),
            SessionCancellationStatus.ValidationFailed => Results.BadRequest(
                RunnerErrorResponse.Create("Validation failed.", outcome.ValidationErrors)),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<(TRequest? Payload, IResult? Error)> ReadBodyAsync<TRequest>(
        HttpRequest httpRequest,
        JsonOptions jsonOptions,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        var (bytes, oversized) = await RunnerRequestBody.ReadBoundedAsync(
            httpRequest.Body,
            RunnerContracts.MaxRequestBodyBytes,
            cancellationToken);

        if (oversized)
        {
            return (null, Results.Json(
                RunnerErrorResponse.Create("Request body exceeds the runner contract's size limit."),
                statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        TRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TRequest>(bytes, jsonOptions.SerializerOptions);
        }
        catch (JsonException)
        {
            return (null, Results.BadRequest(RunnerErrorResponse.Create("Malformed JSON body.")));
        }

        if (payload is null)
        {
            return (null, Results.BadRequest(RunnerErrorResponse.Create("Malformed JSON body.")));
        }

        var contractVersion = payload switch
        {
            RunnerResultRequest result => result.ContractVersion,
            RunnerCancelRequest cancel => cancel.ContractVersion,
            WorkerHeartbeatRequestBody heartbeat => heartbeat.ContractVersion,
            WorkerClaimRequestBody claim => claim.ContractVersion,
            WorkerClaimRenewRequestBody renew => renew.ContractVersion,
            _ => 0
        };

        if (contractVersion != RunnerContracts.ContractVersion)
        {
            return (null, Results.BadRequest(RunnerErrorResponse.Create("Unsupported or missing contract version.")));
        }

        return (payload, null);
    }
}
