using FindFamiliar.Server.Services.Familiar.Chat;
using FindFamiliar.Server.Services.Familiar.Chat.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// The test suite never spends money.
///
/// Unlike every other guard in this project, the thing being protected is not correctness — it is the
/// operator's balance. The talk lane is selected by configuration, and configuration reads environment
/// variables, so a suite run from a shell that had sourced the deployment's EnvironmentFile would
/// inherit a real provider and a real credential and bill a live endpoint for every assertion. Nothing
/// in the code would look wrong; the tests would simply pass, slowly and expensively.
///
/// <see cref="FindFamiliarWebApplicationFactory"/> blanks the selection for exactly that reason. These
/// tests assert the result rather than trusting it, because the failure mode is silent and only shows
/// up on an invoice.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarChatCostIsolationTests(FindFamiliarWebApplicationFactory factory)
{
    /// <summary>
    /// The generator the test host actually resolves is the one that talks to nothing. This is the
    /// assertion that would fail if the isolation in the factory were ever removed.
    /// </summary>
    [Fact]
    public void The_test_host_resolves_the_generator_that_calls_nothing()
    {
        using var scope = factory.Services.CreateScope();

        var generator = scope.ServiceProvider.GetRequiredService<IFamiliarChatGenerator>();

        Assert.IsType<UnconfiguredFamiliarChatGenerator>(generator);
    }

    /// <summary>
    /// And no paid provider is registered at all, so nothing can resolve one by another route — a
    /// future service that took <see cref="IFamiliarChatProvider"/> directly would fail here rather
    /// than quietly start billing.
    /// </summary>
    [Fact]
    public void No_paid_provider_is_registered_in_the_test_host()
    {
        using var scope = factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<IFamiliarChatProvider>());
    }

    /// <summary>
    /// The rule the factory relies on, asserted directly: a blank provider name never selects a paid
    /// endpoint, whatever key happens to be present in the environment.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void A_blank_provider_is_never_configured(string? provider)
    {
        var options = new FamiliarChatOptions
        {
            Provider = provider,
            ApiKeyVariable = "SOME_KEY"
        };

        Assert.False(options.IsConfigured(_ => "a-key-that-is-present-but-must-not-be-used"));
    }
}
