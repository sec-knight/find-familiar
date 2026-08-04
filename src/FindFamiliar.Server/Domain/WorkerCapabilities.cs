namespace FindFamiliar.Server.Domain;

/// <summary>
/// Parses and canonicalizes the comma-separated role list stored on <see cref="Worker.Capabilities"/>.
/// Unknown role names are dropped rather than rejected, so a newer worker reporting a role this
/// server does not know cannot be granted work for it.
/// </summary>
public static class WorkerCapabilities
{
    public const int MaxLength = 200;

    public static IReadOnlyList<AgentSessionRole> Parse(string? capabilities)
    {
        if (string.IsNullOrWhiteSpace(capabilities))
        {
            return [];
        }

        var roles = new List<AgentSessionRole>();

        foreach (var candidate in capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<AgentSessionRole>(candidate, ignoreCase: true, out var role) && !roles.Contains(role))
            {
                roles.Add(role);
            }
        }

        return roles;
    }

    /// <summary>
    /// Renders a role list back to the stored form, in declaration order, so the persisted string
    /// is canonical regardless of the order or casing the worker reported.
    /// </summary>
    public static string Format(IEnumerable<AgentSessionRole> roles)
    {
        var distinct = roles.Distinct().OrderBy(role => (int)role).Select(role => role.ToString());
        return string.Join(",", distinct);
    }
}
