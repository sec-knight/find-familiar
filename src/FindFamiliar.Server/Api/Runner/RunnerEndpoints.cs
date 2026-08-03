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
                payload.ArtifactContent),
            cancellationToken);

        return outcome.Status switch
        {
            SessionResultCaptureStatus.Success => Results.NoContent(),
            SessionResultCaptureStatus.NotFound => Results.NotFound(RunnerErrorResponse.Create("Unknown task or session.")),
            SessionResultCaptureStatus.NotStarted => Results.Conflict(RunnerErrorResponse.Create(
                "This session is no longer Started. A result can only be captured once for a Started session.")),
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
            new SessionCancellationRequest(taskId, sessionId, payload!.Reason),
            cancellationToken);

        return outcome.Status switch
        {
            SessionCancellationStatus.Success => Results.NoContent(),
            SessionCancellationStatus.NotFound => Results.NotFound(RunnerErrorResponse.Create("Unknown task or session.")),
            SessionCancellationStatus.NotStarted => Results.Conflict(RunnerErrorResponse.Create(
                "This session is no longer Started. Only a Started session can be cancelled.")),
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
            _ => 0
        };

        if (contractVersion != RunnerContracts.ContractVersion)
        {
            return (null, Results.BadRequest(RunnerErrorResponse.Create("Unsupported or missing contract version.")));
        }

        return (payload, null);
    }
}
