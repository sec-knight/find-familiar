using FindFamiliar.Server.Services.Familiar;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The behaviour contract's load-bearing properties.
///
/// These assert <b>properties</b> of the text, never equality against a second copy of it. A test
/// holding its own copy of the contract would be the duplicate the contract exists to avoid, and it
/// would pass forever while the shipped text drifted underneath it.
/// </summary>
public sealed class FamiliarBehaviorContractTests
{
    private static string Text => FamiliarBehaviorContract.Text;

    [Fact]
    public void The_contract_is_substantial_and_present()
    {
        Assert.False(string.IsNullOrWhiteSpace(Text));
        Assert.True(Text.Length > 1_000, "A contract this short cannot carry the rules §6 requires.");
    }

    /// <summary>
    /// The three registers, named explicitly. Without these the Familiar can present an inference as
    /// a record, which is the specific failure this feature risks.
    /// </summary>
    [Fact]
    public void The_contract_names_three_registers()
    {
        Assert.Contains("Recorded", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Inferred", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unknown", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never present an inference as a record", Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_contract_confines_the_familiar_to_the_snapshot_and_conversation()
    {
        Assert.Contains("snapshot", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conversation", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Anything not in the snapshot is unknown", Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_contract_requires_limitations_to_be_repeated()
    {
        Assert.Contains("limitation", Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Familiar may propose; only persisted state says what happened.</summary>
    [Fact]
    public void The_contract_forbids_claiming_an_action_occurred()
    {
        Assert.Contains("never say that an action happened", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot start a session", Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_contract_caps_recommendations_at_three()
    {
        Assert.Contains("at most three next steps", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, FamiliarBehaviorContract.MaxRecommendations);

        // The floor and the provider answer must not disagree about how much a person is asked to hold.
        Assert.Equal(FamiliarSummaryWriter.MaxNextSteps, FamiliarBehaviorContract.MaxRecommendations);
    }

    [Fact]
    public void The_contract_forbids_urls_commands_and_paths()
    {
        Assert.Contains("Do not emit URLs", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not emit shell commands", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not emit file paths", Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The contract must not itself contain a URL or a path — it is the example the model reads, and
    /// an instruction not to emit URLs that contains one teaches the opposite of what it says.
    /// </summary>
    [Fact]
    public void The_contract_contains_no_url_or_absolute_path()
    {
        Assert.DoesNotContain("http://", Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/srv/", Text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_contract_names_the_two_supported_actions_and_refuses_the_rest()
    {
        Assert.Contains("creating a task", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planner session", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at most one proposed action", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("say plainly that you cannot do that", Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Citations are only worth accepting if the contract asks for ids from the snapshot.</summary>
    [Fact]
    public void The_contract_requires_citations_to_come_from_the_snapshot()
    {
        Assert.Contains("Cite only", Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not invent one", Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The contract must be the only copy. Nothing in the repository outside this file may hold the
    /// text, or the two will drift and the wrong one will win the argument.
    /// </summary>
    [Fact]
    public void The_contract_has_exactly_one_copy_in_the_repository()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(root);

        // Taken from the shipped text at runtime rather than written out here — a literal copy in
        // this file would be the second copy the test exists to forbid, and it would keep passing
        // while the two drifted apart.
        var fingerprint = Fingerprint(Text);

        var holders = Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(fingerprint, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["FamiliarBehaviorContract.cs"], holders);
    }

    /// <summary>
    /// The longest single line of the contract: distinctive enough that no other file could hold it
    /// by accident, and taken from the shipped text so it cannot go stale.
    /// </summary>
    private static string Fingerprint(string text) =>
        text.Split('\n')
            .Select(line => line.Trim())
            .OrderByDescending(line => line.Length)
            .First();

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, "FindFamiliar.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
