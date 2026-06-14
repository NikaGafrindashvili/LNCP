using System.Globalization;

namespace LNCP.Protocol;

public sealed record DiscoveryMessage(Guid RequestId, string Recipient, DateTimeOffset DeadlineUtc, int TcpPort)
{
    public static DiscoveryMessage FromMessage(LncpMessage message)
    {
        if (message.Type != LncpMessageType.Discover)
        {
            throw new ProtocolException("Expected DISCOVER message.");
        }

        var requestId = ParseGuid(message.RequiredHeader(ProtocolConstants.HeaderRequestId));
        var recipient = message.RequiredHeader(ProtocolConstants.HeaderRecipient);
        var deadline = ParseDeadline(message.RequiredHeader(ProtocolConstants.HeaderDeadlineUtc));
        var tcpPort = ParsePort(message.RequiredHeader(ProtocolConstants.HeaderTcpPort));
        ValidateNickname(recipient);

        return new DiscoveryMessage(requestId, recipient, deadline, tcpPort);
    }

    public LncpMessage ToMessage()
    {
        ValidateNickname(Recipient);

        return new LncpMessage(
            LncpMessageType.Discover,
            new Dictionary<string, string>
            {
                [ProtocolConstants.HeaderRequestId] = RequestId.ToString("D"),
                [ProtocolConstants.HeaderRecipient] = Recipient,
                [ProtocolConstants.HeaderDeadlineUtc] = DeadlineUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                [ProtocolConstants.HeaderTcpPort] = TcpPort.ToString(CultureInfo.InvariantCulture)
            });
    }

    public static Guid ParseGuid(string value)
    {
        return Guid.TryParse(value, out var id)
            ? id
            : throw new ProtocolException($"Invalid UUID '{value}'.");
    }

    public static DateTimeOffset ParseDeadline(string value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var deadline)
            ? deadline.ToUniversalTime()
            : throw new ProtocolException($"Invalid UTC deadline '{value}'.");
    }

    public static int ParsePort(string value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : throw new ProtocolException($"Invalid TCP port '{value}'.");
    }

    public static void ValidateNickname(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c)))
        {
            throw new ProtocolException("Nickname must be non-empty printable text.");
        }
    }
}
