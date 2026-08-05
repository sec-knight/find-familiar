using System.Text.Json;
using System.Text.Json.Serialization;

namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// The one serialized form of a <see cref="ProjectSnapshot"/>.
///
/// There is a single configuration here rather than one per caller because the number that decides
/// whether a project is sent to a reasoning provider is the length of a string, and two serializers
/// that disagree by a comma produce two different budgets. The reduction policy measures with this;
/// the provider boundary, when it exists, will serialize with this; anything that wants to show a
/// snapshot as text uses this. A second <see cref="JsonSerializerOptions"/> in production code would
/// silently reintroduce the disagreement.
///
/// <see cref="Options"/> is read-only, so a caller cannot change the contract for everyone else by
/// adding a converter to it.
/// </summary>
public static class ProjectSnapshotSerialization
{
    /// <summary>
    /// The canonical options: compact, with enums written as their names.
    ///
    /// Names rather than numbers because the serialized snapshot is read by people and, later, by a
    /// model — <c>"Blocked"</c> carries the meaning that <c>3</c> does not, and a reordering of an
    /// enum's members must not change what a snapshot says.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>The snapshot's canonical serialized form.</summary>
    public static string Serialize(ProjectSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);

    /// <summary>
    /// The snapshot as it is measured: the two size fields and the observation time held at fixed
    /// placeholders.
    ///
    /// <see cref="ProjectSnapshot.EstimatedCharacters"/> and
    /// <see cref="ProjectSnapshot.IsWithinBudget"/> would otherwise depend on their own result. The
    /// timestamp would make the same project measure differently on two reads, because a serialized
    /// instant is one to seven characters shorter when the clock lands on a round number, and a
    /// budget that moves with the clock drops a section on one page load and keeps it on the next.
    /// </summary>
    public static ProjectSnapshot ForMeasurement(ProjectSnapshot snapshot) =>
        snapshot with
        {
            EstimatedCharacters = 0,
            IsWithinBudget = true,
            ObservedAt = default
        };

    /// <summary>
    /// The deterministic serialized-size estimate used for snapshot reduction, in characters.
    ///
    /// It is an estimate, not the exact length of the snapshot as it will finally be written: the
    /// placeholders in <see cref="ForMeasurement"/> stand in for values whose real width is either
    /// self-referential or clock-dependent, so a fully populated snapshot may serialize a small,
    /// bounded number of characters longer than this. Characters, not tokens, because counting tokens
    /// means asking a provider and this must work with none configured.
    ///
    /// The supported invariant: <see cref="ProjectSnapshot.EstimatedCharacters"/> is the
    /// deterministic serialized-size estimate used for snapshot reduction. The final provider
    /// envelope must be serialized and checked again immediately before transmission.
    /// </summary>
    public static int Measure(ProjectSnapshot snapshot) =>
        Serialize(ForMeasurement(snapshot)).Length;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };

        // populateMissingResolver: the default reflection-based resolver is what this application
        // serializes with everywhere else, and locking the options in without one would throw.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
