using System.Buffers.Binary;
using System.Text.Json;

namespace LanHub.Core.Protocol;

public static class FrameCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), json.Length);
        json.CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadJsonAsync<T>(Stream stream, CancellationToken ct = default)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len is < 0 or > 1_000_000)
            throw new InvalidDataException($"Invalid frame length: {len}");

        var payload = new byte[len];
        await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    public static async Task WriteEncryptedAsync(Stream stream, byte[] sessionKey, object value, CancellationToken ct = default)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var sealedBytes = Crypto.SessionCrypto.Seal(sessionKey, plain);
        var frame = new byte[4 + sealedBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), sealedBytes.Length);
        sealedBytes.CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadEncryptedAsync<T>(Stream stream, byte[] sessionKey, CancellationToken ct = default)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len is < 12 + 16 or > 1_000_000)
            throw new InvalidDataException($"Invalid encrypted frame length: {len}");

        var sealedBytes = new byte[len];
        await ReadExactAsync(stream, sealedBytes, ct).ConfigureAwait(false);
        var plain = Crypto.SessionCrypto.Open(sessionKey, sealedBytes);
        return JsonSerializer.Deserialize<T>(plain, JsonOptions);
    }

    public static T? FromJson<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, JsonOptions);

    public static byte[] ToUtf8Json<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }
}
