using Microsoft.Data.Sqlite;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// Deterministic teardown for the temporary directories that back test SQLite databases.
///
/// On Windows a file handle is not released the instant its owning object is disposed: a pooled
/// SQLite connection keeps the database file open, and the OS may still hold the handle briefly
/// after the process releases it. Deleting the directory in that window throws
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> *after* every assertion in
/// the test has already passed, turning a green test into a failed run.
///
/// The fix is release-then-retry, not sleep-then-hope:
/// 1. <see cref="SqliteConnection.ClearAllPools"/> closes pooled connections deterministically,
///    which is what actually releases the handle in the overwhelming majority of cases;
/// 2. a bounded retry absorbs the short OS-level handle-release lag that can remain.
///
/// The retry is bounded and the final failure is rethrown — a directory that is still locked after
/// the full budget is a real leak, and this helper must never hide it.
/// </summary>
public static class TemporaryDirectoryCleanup
{
    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public static void Delete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        SqliteConnection.ClearAllPools();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ClearDeletionBlockingAttributes(directory);
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < MaxAttempts)
            {
                // A finalizer may still hold the last reference to a connection the test never
                // disposed explicitly; run it, then clear the pools it just returned to.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                SqliteConnection.ClearAllPools();
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static void ClearDeletionBlockingAttributes(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(child, FileAttributes.Directory);
        }

        File.SetAttributes(directory, FileAttributes.Directory);
    }
}
