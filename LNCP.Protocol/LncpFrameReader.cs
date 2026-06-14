using System.Text;

namespace LNCP.Protocol;

public static class LncpFrameReader
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static async Task<LncpMessage> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var startLine = await ReadLineAsync(stream, cancellationToken);
        if (startLine is null)
        {
            throw new ProtocolException("Connection closed before an LNCP frame was received.");
        }

        return await ReadMessageAfterStartLineAsync(stream, startLine, cancellationToken);
    }

    public static LncpMessage ParseDatagram(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return ReadAsync(stream).GetAwaiter().GetResult();
    }

    private static async Task<LncpMessage> ReadMessageAfterStartLineAsync(Stream stream, string startLine, CancellationToken cancellationToken)
    {
        var parts = startLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] != ProtocolConstants.Version)
        {
            throw new ProtocolException($"Invalid LNCP start line '{startLine}'.");
        }

        var type = LncpMessage.ParseWireType(parts[1]);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerBytes = startLine.Length;

        while (true)
        {
            var line = await ReadLineAsync(stream, cancellationToken);
            if (line is null)
            {
                throw new ProtocolException("Connection closed while reading headers.");
            }

            headerBytes += line.Length;
            if (headerBytes > ProtocolConstants.MaxHeaderBytes)
            {
                throw new ProtocolException($"Headers exceed {ProtocolConstants.MaxHeaderBytes} bytes.");
            }

            if (line.Length == 0)
            {
                break;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new ProtocolException($"Malformed header line '{line}'.");
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (headers.ContainsKey(name))
            {
                throw new ProtocolException($"Duplicate header '{name}'.");
            }

            headers[name] = value;
        }

        var body = string.Empty;
        if (type == LncpMessageType.Text)
        {
            if (!headers.TryGetValue(ProtocolConstants.HeaderLength, out var rawLength) ||
                !int.TryParse(rawLength, out var length) ||
                length < 0 ||
                length > ProtocolConstants.MaxBodyBytes)
            {
                throw new ProtocolException("TEXT frame contains an invalid Length header.");
            }

            var bodyBytes = await ReadExactAsync(stream, length, cancellationToken);
            body = Utf8.GetString(bodyBytes);
        }
        else if (headers.ContainsKey(ProtocolConstants.HeaderLength))
        {
            throw new ProtocolException("Only TEXT frames may include a Length header.");
        }

        return new LncpMessage(type, headers, body);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : DecodeLine(bytes);
            }

            if (buffer[0] == (byte)'\n')
            {
                return DecodeLine(bytes);
            }

            bytes.Add(buffer[0]);
            if (bytes.Count > ProtocolConstants.MaxHeaderBytes)
            {
                throw new ProtocolException("Line exceeds maximum supported size.");
            }
        }
    }

    private static string DecodeLine(List<byte> bytes)
    {
        if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
        {
            bytes.RemoveAt(bytes.Count - 1);
        }

        return Utf8.GetString(bytes.ToArray());
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;

        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                throw new ProtocolException("Connection closed while reading body.");
            }

            offset += read;
        }

        return buffer;
    }
}
