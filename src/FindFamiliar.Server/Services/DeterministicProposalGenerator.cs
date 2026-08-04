using System.Globalization;
using System.Text;
using FindFamiliar.Server.Domain;

namespace FindFamiliar.Server.Services;

/// <summary>A candidate the generator may propose. Only active projects are ever supplied.</summary>
public sealed record ProposalProjectCandidate(Guid Id, string Name);

public enum ProposalProjectResolution
{
    /// <summary>Exactly one active project's complete name appears in the request.</summary>
    MatchedByName,

    /// <summary>No name matched, and exactly one active project exists.</summary>
    OnlyActiveProject,

    /// <summary>More than one project name appears in the request; the user must choose.</summary>
    AmbiguousNameMatch,

    /// <summary>No name matched and more than one active project exists; the user must choose.</summary>
    NoMatch,

    /// <summary>There is nothing to propose because no active project exists.</summary>
    NoActiveProjects
}

public sealed record ProposedProject(ProposalProjectResolution Resolution, Guid? ProjectId)
{
    public bool IsResolved => ProjectId.HasValue;
}

/// <summary>
/// Sprint 08's proposal engine (ADR-0009).
///
/// Deliberately deterministic and pure: given the same request and the same candidate list it
/// always produces the same proposal. No model is called before the user approves, so this type
/// takes no dependency on a provider, the database, or the clock. Matching happens in memory over
/// candidates the caller already loaded, so it can never inherit a database collation quirk.
/// </summary>
public static class DeterministicProposalGenerator
{
    /// <summary>Upper bound on candidate projects examined for one request.</summary>
    public const int MaxCandidateProjects = 200;

    public const int MaxRequestLength = 4_000;

    public const int MaxTitleLength = WorkProposal.MaxTitleLength;

    /// <summary>
    /// Resolves the project the request refers to.
    ///
    /// 1. Exactly one candidate's complete normalized name occurs in the normalized request -> that project.
    /// 2. No name occurs and exactly one candidate exists -> that project.
    /// 3. Anything else stays unresolved. Multiple matches are never silently narrowed.
    /// </summary>
    public static ProposedProject ResolveProject(string request, IReadOnlyList<ProposalProjectCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return new ProposedProject(ProposalProjectResolution.NoActiveProjects, null);
        }

        var normalizedRequest = Normalize(request ?? string.Empty);

        var matches = candidates
            .Where(candidate => ContainsCompleteName(normalizedRequest, Normalize(candidate.Name)))
            .Select(candidate => candidate.Id)
            .Distinct()
            .ToList();

        return matches.Count switch
        {
            1 => new ProposedProject(ProposalProjectResolution.MatchedByName, matches[0]),
            > 1 => new ProposedProject(ProposalProjectResolution.AmbiguousNameMatch, null),
            _ when candidates.Count == 1 =>
                new ProposedProject(ProposalProjectResolution.OnlyActiveProject, candidates[0].Id),
            _ => new ProposedProject(ProposalProjectResolution.NoMatch, null)
        };
    }

    /// <summary>
    /// The proposed task title: the first non-empty line of the trimmed request, bounded to
    /// <see cref="MaxTitleLength"/> characters without cutting a surrogate pair or combining
    /// sequence in half.
    /// </summary>
    public static string BuildTitle(string request)
    {
        var trimmed = (request ?? string.Empty).Trim();

        var firstLine = string.Empty;
        foreach (var line in trimmed.Split('\n'))
        {
            var candidate = line.Trim('\r', ' ', '\t');
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                firstLine = candidate.Trim();
                break;
            }
        }

        return TruncateOnTextElementBoundary(firstLine, MaxTitleLength);
    }

    /// <summary>The proposed requested outcome: the full trimmed request, bounded.</summary>
    public static string BuildRequestedOutcome(string request) =>
        TruncateOnTextElementBoundary((request ?? string.Empty).Trim(), WorkProposal.MaxRequestedOutcomeLength);

    /// <summary>
    /// Truncates without ever emitting half of a surrogate pair or splitting a grapheme cluster,
    /// so an emoji or combining sequence at the boundary stays a valid, renderable sequence.
    /// </summary>
    public static string TruncateOnTextElementBoundary(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var lastSafeLength = 0;

        while (enumerator.MoveNext())
        {
            var end = enumerator.ElementIndex + ((string)enumerator.Current).Length;
            if (end > maxLength)
            {
                break;
            }

            lastSafeLength = end;
        }

        return value[..lastSafeLength];
    }

    /// <summary>
    /// Lowercases invariantly and collapses every run of whitespace to a single space, so
    /// "Find  Familiar" and "find familiar" normalize identically. Ordinal throughout: no culture
    /// and no collation participates in the decision.
    /// </summary>
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when the complete name occurs in the request delimited by something other than a
    /// letter or digit. Requiring the boundary is what stops the project "Find" from claiming a
    /// request about a "Finder" — a partial word is not a complete name.
    /// </summary>
    private static bool ContainsCompleteName(string normalizedRequest, string normalizedName)
    {
        if (normalizedName.Length == 0 || normalizedRequest.Length < normalizedName.Length)
        {
            return false;
        }

        var searchFrom = 0;
        while (searchFrom <= normalizedRequest.Length - normalizedName.Length)
        {
            var index = normalizedRequest.IndexOf(normalizedName, searchFrom, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var startsCleanly = index == 0 || !char.IsLetterOrDigit(normalizedRequest[index - 1]);
            var endIndex = index + normalizedName.Length;
            var endsCleanly = endIndex == normalizedRequest.Length
                || !char.IsLetterOrDigit(normalizedRequest[endIndex]);

            if (startsCleanly && endsCleanly)
            {
                return true;
            }

            searchFrom = index + 1;
        }

        return false;
    }
}
