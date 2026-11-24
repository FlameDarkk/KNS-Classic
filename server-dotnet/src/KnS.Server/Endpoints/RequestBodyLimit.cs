namespace KnS.Server.Endpoints;

/// <summary>Reads HTTP request bodies with a hard byte ceiling during streaming, including chunked transfers.</summary>
internal static class RequestBodyLimit
{
    public const int MaxAccountBytes = 4 * 1024;
    public const int MaxAdminBytes = 1024 * 1024;
    public const int MaxGatewayBytes = 1024 * 1024;

    /// <summary>Returns the body bytes, or null when the body exceeds <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]?> ReadAsync(HttpRequest request, int maxBytes)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        int initialCapacity = request.ContentLength is > 0 and <= 65536 ? (int)request.ContentLength.Value : 0;
        using var buffer = new MemoryStream(initialCapacity);
        byte[] chunk = new byte[Math.Min(maxBytes + 1, 16384)];
        while (true)
        {
            int remaining = maxBytes - (int)buffer.Length + 1;
            int read = await request.Body.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                request.HttpContext.RequestAborted);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }
}
