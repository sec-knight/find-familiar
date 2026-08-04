// Deterministic fake Claude runtime for Sprint 06.5's adapter tests. It emits the same
// --output-format json envelope shape the real CLI produces, so no automated test ever calls the
// paid live provider. All output is obviously synthetic and must never be presented as a real AI
// response, review, or approval.
//
// Mode is selected through FAKE_CLAUDE_MODE:
//   success           - (default) valid envelope, exit 0.
//   nonzero           - exit 1 with no usable output.
//   malformed         - invalid JSON on stdout, exit 0.
//   is-error          - well-formed envelope with is_error true, exit 0.
//   permission-denial - valid-looking envelope carrying a non-empty permission_denials array.
//   oversized         - stdout larger than the adapter's bounded read limit, exit 0.
//   stderr-noise      - large stderr containing sensitive-looking text, plus a valid envelope.
//   timeout           - never exits; the adapter must time out and kill the process tree.
//   echo-argv         - returns its own argv, to prove direct argument passing with no shell.
//   echo-cwd          - returns its working directory, to prove the worktree is the launch dir.
//   echo-env          - reports whether FAMILIAR_RUNNER_TOKEN reached this child process.
//   echo-stdin        - returns the prompt it received, to prove stdin delivery and inertness.

using System.Text;
using System.Text.Json;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var mode = Environment.GetEnvironmentVariable("FAKE_CLAUDE_MODE") ?? "success";

if (mode == "timeout")
{
    await Task.Delay(Timeout.Infinite);
    return 0;
}

var stdin = await Console.In.ReadToEndAsync();

switch (mode)
{
    case "nonzero":
        await Console.Error.WriteLineAsync("fake-claude: simulated non-zero exit.");
        return 1;

    case "malformed":
        await Console.Out.WriteAsync("{ not valid json at all");
        return 0;

    case "is-error":
        await Console.Out.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "error_during_execution",
            is_error = true,
            result = "fake-claude simulated provider error.",
            permission_denials = Array.Empty<object>()
        }));
        return 0;

    case "permission-denial":
        await Console.Out.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "success",
            is_error = false,
            result = "[fake-claude] Dummy fixture text after a blocked tool attempt.",
            permission_denials = new[] { new { tool_name = "Bash", reason = "not in allowed tools" } }
        }));
        return 0;

    case "oversized":
        await Console.Out.WriteAsync(new string('x', 300_000));
        return 0;

    case "stderr-noise":
        await Console.Error.WriteAsync("fake-claude-secret-marker " + new string('e', 100_000));
        await Console.Out.WriteAsync(BuildEnvelope("Dummy fixture result emitted alongside stderr noise."));
        return 0;

    case "echo-argv":
        // A visible separator makes each element distinguishable, so a test can prove the
        // arguments arrived as separate argv entries rather than one re-split command string.
        await Console.Out.WriteAsync(BuildEnvelope("argv:" + string.Join('|', args)));
        return 0;

    case "echo-cwd":
        await Console.Out.WriteAsync(BuildEnvelope("cwd:" + Environment.CurrentDirectory));
        return 0;

    case "echo-env":
        var tokenPresent = Environment.GetEnvironmentVariable("FAMILIAR_RUNNER_TOKEN") is not null;
        await Console.Out.WriteAsync(BuildEnvelope($"token-present:{tokenPresent}"));
        return 0;

    case "echo-stdin":
        await Console.Out.WriteAsync(BuildEnvelope("stdin:" + stdin));
        return 0;

    case "success":
    default:
        await Console.Out.WriteAsync(BuildEnvelope($"Deterministic fixture output. Received {stdin.Length} prompt characters."));
        return 0;
}

static string BuildEnvelope(string note) => JsonSerializer.Serialize(new
{
    type = "result",
    subtype = "success",
    is_error = false,
    result = $"[fake-claude] {note} This is dummy fixture output, not a real AI response.",
    permission_denials = Array.Empty<object>(),
    session_id = "00000000-0000-0000-0000-000000000000",
    total_cost_usd = 0.0
});
