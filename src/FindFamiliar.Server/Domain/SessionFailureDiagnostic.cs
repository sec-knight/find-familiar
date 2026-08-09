namespace FindFamiliar.Server.Domain;

/// <summary>Bounded, provider-neutral failure metadata persisted for a terminal worker session.</summary>
public sealed record SessionFailureDiagnostic(
    string Category,
    int? AdapterExitCode,
    bool? ProviderLaunched,
    int? ProviderExitCode,
    string Message)
{
    public const int CategoryMaxLength = 80;
    public const int MessageMaxLength = 300;

    private static readonly HashSet<string> KnownCategories = new(StringComparer.Ordinal)
    {
        "ConfigurationInvalid",
        "InvocationInvalid",
        "WorktreeRejected",
        "WorktreeNotClean",
        "RuntimeLaunchFailed",
        "RuntimeTimeout",
        "RuntimeNonZeroExit",
        "RuntimeOutputInvalid",
        "PermissionDenialReported",
        "RunnerLaunchFailed",
        "RunnerTimeout",
        "AdapterNonZeroExit"
    };

    public static bool TryNormalize(
        SessionFailureDiagnostic? input,
        out SessionFailureDiagnostic? normalized,
        out string? error)
    {
        normalized = null;
        error = null;

        if (input is null)
        {
            return true;
        }

        var category = input.Category?.Trim() ?? string.Empty;
        if (!KnownCategories.Contains(category))
        {
            error = "Diagnostic category is not recognised.";
            return false;
        }

        if (input.AdapterExitCode is < 0 or > 255)
        {
            error = "Adapter exit code must be between 0 and 255 when supplied.";
            return false;
        }

        if (input.ProviderExitCode is < 0 or > 255)
        {
            error = "Provider exit code must be between 0 and 255 when supplied.";
            return false;
        }

        var message = (input.Message ?? string.Empty).Trim();
        if (message.Length == 0 || message.Length > MessageMaxLength || message.Any(char.IsControl))
        {
            error = $"Diagnostic message must be 1 to {MessageMaxLength} printable characters.";
            return false;
        }

        if (category.Length > CategoryMaxLength)
        {
            error = $"Diagnostic category must be {CategoryMaxLength} characters or fewer.";
            return false;
        }

        // Never persist the worker-supplied prose. The server stores only a canonical category-based
        // sentence; raw provider stderr, prompts and transcripts therefore cannot cross this boundary.
        var canonicalMessage = input.ProviderLaunched switch
        {
            false => "Adapter preflight rejected the session before the provider was launched.",
            true when input.ProviderExitCode is { } providerExit => $"The provider runtime exited with code {providerExit}.",
            true => "The provider runtime failed after launch.",
            _ => "The provider launch state was not reported."
        };

        normalized = new SessionFailureDiagnostic(
            category, input.AdapterExitCode, input.ProviderLaunched, input.ProviderExitCode, canonicalMessage);
        return true;
    }

    public string ToCancellationReason(AgentSessionRole role)
    {
        var adapter = AdapterExitCode is { } code ? $" (adapter exit {code})" : string.Empty;

        return ProviderLaunched switch
        {
            false => $"{role} could not start: {Category}{adapter}. Provider was not launched.",
            true when ProviderExitCode is { } providerCode =>
                $"{role} provider failed: {Category}{adapter}; provider exit {providerCode}.",
            true => $"{role} provider failed: {Category}{adapter}.",
            _ => $"{role} session failed: {Category}{adapter}. Provider launch state was not reported."
        };
    }
}
