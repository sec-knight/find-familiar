using System.Net;
using System.Net.Http.Headers;
using FindFamiliar.Server.Tests.Infrastructure;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// The post-commit trigger.
///
/// It asks for a capture and returns immediately, because it is called from inside <c>git commit</c>:
/// a hook that waits on a database write makes committing feel slow and gets deleted, and a snapshot
/// nobody triggers is the arrangement this replaced.
///
/// It is behind the runner bridge token because it is the same kind of machine caller, and a second
/// credential to distribute would be a second credential to leak.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class RepositorySnapshotEndpointTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task An_authenticated_trigger_is_accepted_without_waiting_for_the_capture()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/repository/snapshot");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", FindFamiliarWebApplicationFactory.RunnerBridgeTestToken);

        var response = await client.SendAsync(request);

        // Accepted, not OK: nothing has been written yet, and saying otherwise would let a caller read
        // the entry and find the old one.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_trigger_is_refused()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/repository/snapshot", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
