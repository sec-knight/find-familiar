using System.Collections.Concurrent;

namespace FindFamiliar.Server.Api.Gateway.OAuth;

/// <summary>
/// The one piece of state a signed-artifact authorization server cannot do without: which
/// single-use artifacts have already been used.
///
/// A signature proves this server issued a value; it cannot prove nobody has spent it yet. OAuth 2.1
/// requires an authorization code be redeemable exactly once, and requires refresh tokens issued to
/// public clients be rotated — both of which are statements about history, not about validity.
///
/// <b>In memory, and deliberately not in the database.</b> Every entry is worthless once the artifact
/// it names has expired, which for a code is sixty seconds. Persisting that would mean a table, a
/// migration and a cleanup job to hold data whose entire useful life is shorter than a deploy.
///
/// <b>What that costs, stated plainly:</b> a restart forgets which refresh tokens were already
/// rotated, so a refresh token captured before a restart could be redeemed once after it. That
/// exposure requires an attacker who already holds a refresh token — at which point they hold read
/// access regardless — and closing it would mean persisting token state for a single-user deployment.
/// ADR-0017 records the trade and what would reverse it: a second user, or any tool that writes.
/// </summary>
public sealed class FamiliarOAuthReplayGuard(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, long> _spent = new();

    /// <summary>
    /// Records an identifier as spent and reports whether this caller was the first to do so. Atomic:
    /// two simultaneous redemptions of one code produce exactly one true.
    /// </summary>
    public bool TrySpend(string id, DateTimeOffset expiresAt)
    {
        Prune();

        return _spent.TryAdd(id, expiresAt.ToUnixTimeSeconds());
    }

    private void Prune()
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();

        foreach (var entry in _spent)
        {
            // An entry may be dropped the moment the artifact it names can no longer be presented: an
            // expired code is refused by signature-and-expiry checking before it ever reaches here.
            if (entry.Value <= now)
            {
                _spent.TryRemove(entry.Key, out _);
            }
        }
    }
}
