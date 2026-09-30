using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanChat.Online;

public static class ChatMsgType
{
    public const byte Chat = 1;
    public const byte Presence = 2;
    public const byte Typing = 3;
}

public sealed class ChatWire
{
    [JsonPropertyName("fromId")]
    public string FromId { get; set; } = "";

    [JsonPropertyName("fromName")]
    public string FromName { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("toId")]
    public string? ToId { get; set; }

    [JsonPropertyName("utc")]
    public long UtcUnixMs { get; set; }
}

public static class ChatCodec
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static byte[] EncodeChat(ChatWire msg)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(msg, Opts);
        var buf = new byte[1 + json.Length];
        buf[0] = ChatMsgType.Chat;
        Buffer.BlockCopy(json, 0, buf, 1, json.Length);
        return buf;
    }

    public static byte[] EncodeTyping(string fromId, string? toId)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { fromId, toId }, Opts);
        var buf = new byte[1 + json.Length];
        buf[0] = ChatMsgType.Typing;
        Buffer.BlockCopy(json, 0, buf, 1, json.Length);
        return buf;
    }

    public static (byte type, string json) Decode(byte[] payload)
    {
        if (payload == null || payload.Length < 1) return (0, "{}");
        return (payload[0], Encoding.UTF8.GetString(payload, 1, payload.Length - 1));
    }

    public static T? Parse<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, Opts); }
        catch { return default; }
    }
}
