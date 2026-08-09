// Deterministic fake adapter test fixture for Sprint 06's runner bridge. Implements the
// versioned stdin/stdout contract without a live provider. Contains only obvious dummy values —
// its output must never be presented as a real AI review or human approval.
//
// Mode is selected through FAKE_ADAPTER_MODE:
//   success        - (default) valid single-document result, exit 0.
//   nonzero        - exit 1 with no result written.
//   timeout        - never exits on its own; the runner is expected to time it out and kill it.
//   malformed      - invalid JSON on stdout, exit 0.
//   multiple-json  - two concatenated JSON documents on stdout, exit 0.
//   missing-fields - valid JSON shape but required fields are blank, exit 0.
//   oversized      - stdout larger than the runner's bounded read limit, exit 0.
//   stderr-noise   - large bounded stderr output alongside a valid result, exit 0.
//   echo-env       - reports whether FAMILIAR_RUNNER_TOKEN is present in its own environment,
//                    for the runner's child-environment-scrubbing test.
//   stall-stdin    - never reads stdin at all; proves the runner's timeout bounds the stdin
//                    write itself, not just the post-write wait for exit.
//   delayed-success - waits long enough for worker heartbeat and lease-renewal maintenance.

using System.Text.Json;

var mode = Environment.GetEnvironmentVariable("FAKE_ADAPTER_MODE") ?? "success";
var pidFile = Environment.GetEnvironmentVariable("FAKE_ADAPTER_PID_FILE");
if (!string.IsNullOrWhiteSpace(pidFile))
{
    await File.WriteAllTextAsync(pidFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

if (mode == "stall-stdin")
{
    // Deliberately never touches stdin, so a correct runner must time out the write itself
    // rather than hang waiting for this process to start reading.
    await Task.Delay(Timeout.Infinite);
    return 0;
}

// Every other mode drains stdin first so the runner's write never blocks on a pipe the adapter
// hasn't started reading, regardless of which mode below actually inspects the payload.
var stdin = await Console.In.ReadToEndAsync();

switch (mode)
{
    case "delayed-success":
        await Task.Delay(TimeSpan.FromSeconds(12));
        await Console.Out.WriteAsync(BuildResultJson($"Delayed result. Received {stdin.Length} stdin bytes."));
        return 0;

    case "timeout":
        await Task.Delay(Timeout.Infinite);
        return 0;

    case "nonzero":
        await Console.Error.WriteLineAsync("fake-adapter: simulated non-zero exit.");
        return 1;

    case "malformed":
        await Console.Out.WriteAsync("{ this is not valid json");
        return 0;

    case "multiple-json":
        await Console.Out.WriteAsync(BuildResultJson("First of two documents."));
        await Console.Out.WriteAsync(BuildResultJson("Second of two documents."));
        return 0;

    case "missing-fields":
        await Console.Out.WriteAsync("""{"contractVersion":1,"rawOutput":"","summary":"","artifactTitle":"","artifactContent":""}""");
        return 0;

    case "oversized":
        // Must exceed the runner's bounded read limit, which rose to 1 MB when the complete artifact
        // began travelling with the result. A fixture sized to the old limit would silently stop
        // testing oversized handling and start testing an ordinary large success.
        await Console.Out.WriteAsync(new string('x', 1_200_000));
        return 0;

    case "long-plan":
        // A synthetic Planner artifact well past the 12,000-character excerpt bound, for proving that
        // the complete plan survives the whole runner → capture → retrieval path (ADR-0020).
        await Console.Out.WriteAsync(BuildLongPlanJson());
        return 0;

    case "stderr-noise":
        await Console.Error.WriteAsync(new string('e', 100_000));
        await Console.Out.WriteAsync(BuildResultJson($"Result after stderr noise. Received {stdin.Length} stdin bytes."));
        return 0;

    case "echo-env":
        var tokenPresent = Environment.GetEnvironmentVariable("FAMILIAR_RUNNER_TOKEN") is not null;
        await Console.Out.WriteAsync(BuildResultJson($"token-present:{tokenPresent}"));
        return 0;

    case "success":
    default:
        await Console.Out.WriteAsync(BuildResultJson($"Deterministic fixture output. Received {stdin.Length} stdin bytes."));
        return 0;
}

static string BuildLongPlanJson()
{
    // Head and tail markers bracket the filler so a test can prove it received both ends, which a
    // truncated artifact could not provide.
    var plan = "# Synthetic plan\nPLAN_HEAD_MARKER\n"
        + string.Join('\n', Enumerable.Range(0, 900).Select(index => $"Step {index}: dummy fixture planning line, not a real proposal."))
        + "\nPLAN_TAIL_MARKER\n";

    var result = new
    {
        contractVersion = 1,
        rawOutput = "[fake-adapter] Long plan fixture. This is dummy fixture output, not a real AI response.",
        summary = "Dummy fixture summary for the long-plan fixture — not a real result.",
        artifactTitle = "Fake adapter long plan",
        artifactContent = plan.Length <= 12_000 ? plan : plan[..12_000],
        completeArtifactContent = plan,
        completeArtifactLength = plan.Length
    };
    return JsonSerializer.Serialize(result);
}

static string BuildResultJson(string note)
{
    var result = new
    {
        contractVersion = 1,
        rawOutput = $"[fake-adapter] {note} This is dummy fixture output, not a real AI response.",
        summary = "Dummy fixture summary produced by the deterministic fake adapter — not a real result.",
        artifactTitle = "Fake adapter artifact",
        artifactContent = "Dummy fixture artifact content produced by the deterministic fake adapter for Sprint 06 dogfooding/tests."
    };
    return JsonSerializer.Serialize(result);
}
