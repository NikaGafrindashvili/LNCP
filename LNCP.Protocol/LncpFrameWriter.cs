using System.Text;

namespace LNCP.Protocol;

public static class LncpFrameWriter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static async Task WriteAsync(Stream stream, LncpMessage message, CancellationToken cancellationToken = default)
    {
        var bytes = Serialize(message);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static byte[] SerializeDatagram(LncpMessage message)
    {
        return Serialize(message);
    }

    private static byte[] Serialize(LncpMessage message)
    {
        ValidateHeaders(message);

        var output = new MemoryStream();
        WriteAscii(output, $"{ProtocolConstants.Version} {LncpMessage.ToWireType(message.Type)}\r\n");

        var headers = new Dictionary<string, string>(message.Headers, StringComparer.OrdinalIgnoreCase);
        if (message.Type == LncpMessageType.Text)
        {
            headers[ProtocolConstants.HeaderLength] = Utf8.GetByteCount(message.Body).ToString();
        }

        foreach (var (name, value) in headers)
        {
            if (name.Contains(':') || name.Contains('\r') || name.Contains('\n'))
            {
                throw new ProtocolException($"Invalid header name '{name}'.");
            }

            if (value.Contains('\r') || value.Contains('\n'))
            {
                throw new ProtocolException($"Invalid newline in header '{name}'.");
            }

            WriteAscii(output, $"{name}: {value}\r\n");
        }

        WriteAscii(output, "\r\n");

        if (message.Type == LncpMessageType.Text)
        {
            var bodyBytes = Utf8.GetBytes(message.Body);
            if (bodyBytes.Length > ProtocolConstants.MaxBodyBytes)
            {
                throw new ProtocolException($"Text body exceeds {ProtocolConstants.MaxBodyBytes} bytes.");
            }

            output.Write(bodyBytes);
        }

        return output.ToArray();
    }

    private static void ValidateHeaders(LncpMessage message)
    {
        if (message.Type != LncpMessageType.Text && !string.IsNullOrEmpty(message.Body))
        {
            throw new ProtocolException("Only TEXT messages may include a body.");
        }
    }

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
    }
}
