using System.Collections.ObjectModel;

namespace LNCP.Protocol;

public sealed class LncpMessage
{
    public LncpMessage(LncpMessageType type, IDictionary<string, string>? headers = null, string body = "")
    {
        Type = type;
        Headers = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        Body = body;
    }

    public LncpMessageType Type { get; }

    public Dictionary<string, string> Headers { get; }

    public string Body { get; }

    public IReadOnlyDictionary<string, string> ReadOnlyHeaders => new ReadOnlyDictionary<string, string>(Headers);

    public string RequiredHeader(string name)
    {
        if (!Headers.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ProtocolException($"Missing required header '{name}'.");
        }

        return value;
    }

    public static string ToWireType(LncpMessageType type) => type switch
    {
        LncpMessageType.Discover => "DISCOVER",
        LncpMessageType.Hello => "HELLO",
        LncpMessageType.HandshakeResponse => "HANDSHAKE-RESPONSE",
        LncpMessageType.Text => "TEXT",
        LncpMessageType.Close => "CLOSE",
        _ => throw new ProtocolException($"Unsupported message type '{type}'.")
    };

    public static LncpMessageType ParseWireType(string wireType) => wireType switch
    {
        "DISCOVER" => LncpMessageType.Discover,
        "HELLO" => LncpMessageType.Hello,
        "HANDSHAKE-RESPONSE" => LncpMessageType.HandshakeResponse,
        "TEXT" => LncpMessageType.Text,
        "CLOSE" => LncpMessageType.Close,
        _ => throw new ProtocolException($"Unknown LNCP message type '{wireType}'.")
    };
}
