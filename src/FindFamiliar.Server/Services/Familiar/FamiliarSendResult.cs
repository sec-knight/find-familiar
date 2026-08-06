namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// How a send ended. Expected operational failures are values rather than exceptions, for the reason
/// <c>ProjectSnapshotResult</c> gives: a page that cannot send should say so, not return a 500.
/// </summary>
public enum FamiliarSendStatus
{
    /// <summary>The human message was appended and a Familiar reply followed it.</summary>
    Answered,

    /// <summary>
    /// The human message was appended and a System note explains why no reply followed. The message
    /// is durable either way — that is the whole point of committing it before any provider I/O.
    /// </summary>
    Reported,

    /// <summary>The message was empty, whitespace, or over the cap. Nothing was written.</summary>
    Invalid,

    /// <summary>No project with that id. Nothing was written.</summary>
    ProjectNotFound,

    /// <summary>
    /// The database was busy. Nothing was written, and this is never reported as a competing
    /// decision — no competitor has been established.
    /// </summary>
    DatabaseBusy
}

/// <param name="ValidationMessage">
/// Shown next to the input for <see cref="FamiliarSendStatus.Invalid"/>. Authored here, never
/// derived from provider text or an exception.
/// </param>
public sealed record FamiliarSendResult(
    FamiliarSendStatus Status,
    string? ValidationMessage = null)
{
    public static FamiliarSendResult Answered() => new(FamiliarSendStatus.Answered);

    public static FamiliarSendResult Reported() => new(FamiliarSendStatus.Reported);

    public static FamiliarSendResult Invalid(string message) =>
        new(FamiliarSendStatus.Invalid, message);

    public static FamiliarSendResult ProjectNotFound() => new(FamiliarSendStatus.ProjectNotFound);

    public static FamiliarSendResult DatabaseBusy() => new(FamiliarSendStatus.DatabaseBusy);
}
