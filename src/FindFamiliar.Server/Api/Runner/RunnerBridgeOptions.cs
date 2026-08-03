namespace FindFamiliar.Server.Api.Runner;

/// <summary>
/// Machine-authentication configuration for the runner bridge API, bound from
/// configuration section "RunnerBridge" (for example the environment variable
/// <c>RunnerBridge__Token</c>). Never given a default value in committed appsettings.
/// </summary>
public sealed class RunnerBridgeOptions
{
    public const string SectionName = "RunnerBridge";

    public string? Token { get; set; }
}
