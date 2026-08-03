namespace FindFamiliar.Server.Api.Runner;

internal static class RunnerRequestBody
{
    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes from <paramref name="body"/>. Returns
    /// <c>Oversized: true</c> the instant more than the limit is available, without buffering an
    /// unbounded amount of attacker-controlled input first.
    /// </summary>
    public static async Task<(byte[] Bytes, bool Oversized)> ReadBoundedAsync(
        Stream body,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var read = await body.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                return (Array.Empty<byte>(), true);
            }
        }

        return (buffer.ToArray(), false);
    }
}
