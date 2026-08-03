using FindFamiliar.Runner;

namespace FindFamiliar.Server.Tests.Runner;

public sealed class RunnerArgumentsTests
{
    [Fact]
    public void Valid_arguments_and_environment_parse_successfully()
    {
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var environment = NewEnvironment(token: "a-token", adapterPath: "/path/to/adapter");

        var arguments = RunnerArguments.TryParse(
            ["--base-url", "http://localhost:5000", "--task-id", taskId.ToString(), "--session-id", sessionId.ToString()],
            environment,
            TextWriter.Null);

        Assert.NotNull(arguments);
        Assert.Equal(taskId, arguments.TaskId);
        Assert.Equal(sessionId, arguments.SessionId);
        Assert.Equal("a-token", arguments.FamiliarToken);
        Assert.Equal("/path/to/adapter", arguments.AdapterPath);
        Assert.Equal(TimeSpan.FromSeconds(RunnerArguments.DefaultTimeoutSeconds), arguments.Timeout);
    }

    [Fact]
    public void Missing_required_argument_returns_null()
    {
        var arguments = RunnerArguments.TryParse(
            ["--base-url", "http://localhost:5000"],
            NewEnvironment(),
            TextWriter.Null);

        Assert.Null(arguments);
    }

    [Fact]
    public void Invalid_base_url_returns_null()
    {
        var arguments = RunnerArguments.TryParse(
            ["--base-url", "not-a-url", "--task-id", Guid.NewGuid().ToString(), "--session-id", Guid.NewGuid().ToString()],
            NewEnvironment(),
            TextWriter.Null);

        Assert.Null(arguments);
    }

    [Fact]
    public void Invalid_guid_returns_null()
    {
        var arguments = RunnerArguments.TryParse(
            ["--base-url", "http://localhost:5000", "--task-id", "not-a-guid", "--session-id", Guid.NewGuid().ToString()],
            NewEnvironment(),
            TextWriter.Null);

        Assert.Null(arguments);
    }

    [Fact]
    public void Missing_token_environment_variable_returns_null()
    {
        var environment = NewEnvironment(token: null);

        var arguments = RunnerArguments.TryParse(
            ["--base-url", "http://localhost:5000", "--task-id", Guid.NewGuid().ToString(), "--session-id", Guid.NewGuid().ToString()],
            environment,
            TextWriter.Null);

        Assert.Null(arguments);
    }

    [Fact]
    public void Missing_adapter_path_environment_variable_returns_null()
    {
        var environment = NewEnvironment(adapterPath: null);

        var arguments = RunnerArguments.TryParse(
            ["--base-url", "http://localhost:5000", "--task-id", Guid.NewGuid().ToString(), "--session-id", Guid.NewGuid().ToString()],
            environment,
            TextWriter.Null);

        Assert.Null(arguments);
    }

    [Theory]
    [InlineData("1", RunnerArguments.MinTimeoutSeconds)]
    [InlineData("999999", RunnerArguments.MaxTimeoutSeconds)]
    [InlineData("120", 120)]
    public void Timeout_is_clamped_to_documented_bounds(string requested, int expectedSeconds)
    {
        var environment = NewEnvironment();
        environment[RunnerArguments.TimeoutVariable] = requested;

        var arguments = RunnerArguments.TryParse(
            ["--base-url", "http://localhost:5000", "--task-id", Guid.NewGuid().ToString(), "--session-id", Guid.NewGuid().ToString()],
            environment,
            TextWriter.Null);

        Assert.NotNull(arguments);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), arguments.Timeout);
    }

    [Fact]
    public void Adapter_arguments_are_split_on_whitespace()
    {
        var environment = NewEnvironment();
        environment[RunnerArguments.AdapterArgumentsVariable] = "--flag value --other";

        var arguments = RunnerArguments.TryParse(
            ["--base-url", "http://localhost:5000", "--task-id", Guid.NewGuid().ToString(), "--session-id", Guid.NewGuid().ToString()],
            environment,
            TextWriter.Null);

        Assert.NotNull(arguments);
        Assert.Equal(["--flag", "value", "--other"], arguments.AdapterArguments);
    }

    private static System.Collections.Hashtable NewEnvironment(string? token = "test-token", string? adapterPath = "/path/to/adapter")
    {
        var table = new System.Collections.Hashtable();
        if (token is not null)
        {
            table[RunnerArguments.TokenVariable] = token;
        }

        if (adapterPath is not null)
        {
            table[RunnerArguments.AdapterPathVariable] = adapterPath;
        }

        return table;
    }
}
