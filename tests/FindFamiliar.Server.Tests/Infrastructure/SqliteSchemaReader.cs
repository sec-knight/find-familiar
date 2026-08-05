using Microsoft.Data.Sqlite;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// Reads what a SQLite file actually contains, rather than what EF's model says it should.
///
/// Schema tests that inspect the EF model only prove the model is self-consistent. A migration that
/// creates a different table than the model describes passes those and still ships the wrong
/// database, so the migration and index guards read <c>sqlite_master</c> and <c>PRAGMA</c> output
/// here instead.
/// </summary>
internal static class SqliteSchemaReader
{
    /// <summary>Every table name in the database, excluding SQLite's and EF's own bookkeeping.</summary>
    public static IReadOnlyList<string> TableNames(string connectionString) =>
        Query(
            connectionString,
            """
            SELECT "name" FROM "sqlite_master"
            WHERE "type" = 'table' AND "name" NOT LIKE 'sqlite_%' AND "name" <> '__EFMigrationsHistory'
            ORDER BY "name";
            """);

    /// <summary>
    /// The stored DDL of every table and index, keyed by name. Comparing this map before and after a
    /// migration is what proves an existing table was left structurally untouched.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Definitions(string connectionString)
    {
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "type" || ':' || "name", COALESCE("sql", '')
            FROM "sqlite_master"
            WHERE "type" IN ('table', 'index') AND "name" NOT LIKE 'sqlite_%'
            ORDER BY "name";
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            definitions[reader.GetString(0)] = reader.GetString(1);
        }

        return definitions;
    }

    /// <summary>The stored DDL of one index, or null when it does not exist.</summary>
    public static string? IndexSql(string connectionString, string indexName) =>
        Query(
            connectionString,
            $"""
            SELECT COALESCE("sql", '') FROM "sqlite_master"
            WHERE "type" = 'index' AND "name" = '{indexName}';
            """).SingleOrDefault();

    /// <summary>Every column of every table, as "Table.Column".</summary>
    public static IReadOnlyList<string> QualifiedColumnNames(string connectionString)
    {
        var columns = new List<string>();

        foreach (var table in TableNames(connectionString))
        {
            columns.AddRange(Query(connectionString, $"""SELECT "name" FROM pragma_table_info('{table}');""")
                .Select(column => $"{table}.{column}"));
        }

        return columns;
    }

    private static List<string> Query(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
