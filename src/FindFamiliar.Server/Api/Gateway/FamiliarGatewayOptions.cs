namespace FindFamiliar.Server.Api.Gateway;

/// <summary>
/// The credential an external AI client presents to reach the Familiar, bound from configuration
/// section "FamiliarGateway" — for example the environment variable <c>FamiliarGateway__Token</c>.
/// Never given a value in committed appsettings.
///
/// <b>A separate secret from the runner bridge, deliberately.</b> They look alike and they are not:
/// the runner token is handed to a process on this machine or on the user's tailnet, and this one is
/// handed to a frontier vendor's servers and travels the public internet on every call. Sharing one
/// credential across those two trust domains would mean a leak at either end handing away both, and
/// would make rotating one impossible without breaking the other. Two tokens cost one more line in an
/// env file.
///
/// <b>Off unless configured.</b> There is no default and no development fallback. An unset token
/// leaves the gateway refusing every request rather than accepting any, which is the correct posture
/// for a surface whose whole purpose is to be reachable from outside.
/// </summary>
public sealed class FamiliarGatewayOptions
{
    public const string SectionName = "FamiliarGateway";

    public string? Token { get; set; }

    /// <summary>
    /// Whether the gateway is mapped at all.
    ///
    /// Separate from the token so a deployment can state "no external body may reach this" as a fact
    /// about the deployment rather than as the absence of a setting. With this false the routes do
    /// not exist, and a probe cannot tell a disabled gateway from a server that never had one.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The largest request body the gateway will read, in bytes.
    ///
    /// Small on purpose. Every operation here takes a short query and some ids; a megabyte of JSON is
    /// not a question this can answer, and reading it before finding that out is the cheapest denial
    /// of service there is.
    /// </summary>
    public int MaxRequestBytes { get; set; } = 16 * 1024;

    /// <summary>
    /// A token short enough to be guessable is worse than none, because it looks like security. This
    /// is not a strength estimate — it is a floor that rejects a placeholder somebody meant to
    /// replace.
    /// </summary>
    public const int MinimumTokenLength = 32;

    public bool IsConfigured() =>
        Enabled
        && !string.IsNullOrWhiteSpace(Token)
        && Token.Trim().Length >= MinimumTokenLength;
}
