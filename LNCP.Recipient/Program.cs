using System.Globalization;
using System.Net;
using System.Net.Sockets;
using LNCP.Protocol;

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return 0;
}

if (!options.TryGet("nickname", out var nickname))
{
    Console.Error.WriteLine("Missing required option --nickname.");
    PrintUsage();
    return 2;
}

try
{
    DiscoveryMessage.ValidateNickname(nickname);

    var discoveryPort = options.GetInt("discovery-port", ProtocolConstants.DefaultDiscoveryPort);
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    using var udp = new UdpClient(AddressFamily.InterNetwork);
    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
    udp.EnableBroadcast = true;

    Console.WriteLine($"LNCP recipient '{nickname}' listening on UDP port {discoveryPort}.");
    Console.WriteLine("Waiting for matching discovery broadcasts...");

    while (!shutdown.IsCancellationRequested)
    {
        UdpReceiveResult received;
        try
        {
            received = await udp.ReceiveAsync(shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        DiscoveryMessage discovery;
        try
        {
            var message = LncpFrameReader.ParseDatagram(received.Buffer);
            discovery = DiscoveryMessage.FromMessage(message);
        }
        catch (ProtocolException ex)
        {
            Console.WriteLine($"Ignoring malformed discovery from {received.RemoteEndPoint}: {ex.Message}");
            continue;
        }

        if (!discovery.Recipient.Equals(nickname, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Ignoring discovery for '{discovery.Recipient}' from {received.RemoteEndPoint.Address}.");
            continue;
        }

        if (DateTimeOffset.UtcNow > discovery.DeadlineUtc)
        {
            Console.WriteLine($"Ignoring expired discovery {discovery.RequestId}.");
            continue;
        }

        Console.Write($"Accept request from {received.RemoteEndPoint.Address}:{discovery.TcpPort}? [y/N] ");
        var answer = Console.ReadLine();
        if (answer is null || !answer.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Request declined.");
            continue;
        }

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var remaining = discovery.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Console.WriteLine("Request expired before TCP connection could start.");
            continue;
        }

        deadlineCts.CancelAfter(remaining);
        await ConnectHandshakeAndChatAsync(received.RemoteEndPoint.Address, discovery, deadlineCts.Token);
        return 0;
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Recipient cancelled.");
    return 1;
}
catch (Exception ex) when (ex is ProtocolException or SocketException or FormatException or IOException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static async Task ConnectHandshakeAndChatAsync(IPAddress initiatorAddress, DiscoveryMessage discovery, CancellationToken cancellationToken)
{
    using var client = new TcpClient(AddressFamily.InterNetwork);
    Console.WriteLine($"Connecting to {initiatorAddress}:{discovery.TcpPort}...");
    await client.ConnectAsync(initiatorAddress, discovery.TcpPort, cancellationToken);
    client.NoDelay = true;

    await using var stream = client.GetStream();
    await LncpFrameWriter.WriteAsync(
        stream,
        new LncpMessage(
            LncpMessageType.Hello,
            new Dictionary<string, string> { [ProtocolConstants.HeaderRequestId] = discovery.RequestId.ToString("D") }),
        cancellationToken);

    var response = await LncpFrameReader.ReadAsync(stream, cancellationToken);
    if (response.Type != LncpMessageType.HandshakeResponse)
    {
        throw new ProtocolException($"Expected HANDSHAKE-RESPONSE, received {response.Type}.");
    }

    var status = response.RequiredHeader(ProtocolConstants.HeaderStatus);
    if (!status.Equals("ACCEPTED", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Handshake rejected: {GetReason(response)}");
        return;
    }

    Console.WriteLine("Handshake accepted. Type /quit to close.");
    await ChatAsReceiverFirstAsync(stream, cancellationToken);
}

static async Task ChatAsReceiverFirstAsync(Stream stream, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var inbound = await LncpFrameReader.ReadAsync(stream, cancellationToken);
        if (inbound.Type == LncpMessageType.Close)
        {
            Console.WriteLine($"peer closed: {GetReason(inbound)}");
            return;
        }

        if (inbound.Type != LncpMessageType.Text)
        {
            await SendCloseAsync(stream, $"Unexpected {inbound.Type} frame.", cancellationToken);
            throw new ProtocolException($"Expected TEXT or CLOSE, received {inbound.Type}.");
        }

        Console.WriteLine($"peer> {inbound.Body}");
        Console.Write("you> ");
        var outbound = Console.ReadLine();
        if (outbound is null || outbound.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            await SendCloseAsync(stream, "User requested close.", cancellationToken);
            Console.WriteLine("Connection closed.");
            return;
        }

        await LncpFrameWriter.WriteAsync(stream, new LncpMessage(LncpMessageType.Text, body: outbound), cancellationToken);
    }
}

static async Task SendCloseAsync(Stream stream, string reason, CancellationToken cancellationToken)
{
    await LncpFrameWriter.WriteAsync(
        stream,
        new LncpMessage(LncpMessageType.Close, new Dictionary<string, string> { [ProtocolConstants.HeaderReason] = reason }),
        cancellationToken);
}

static string GetReason(LncpMessage message)
{
    return message.Headers.TryGetValue(ProtocolConstants.HeaderReason, out var reason) ? reason : "no reason supplied";
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project LNCP.Recipient -- --nickname <nickname> [--discovery-port 45678]");
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public bool ShowHelp { get; private init; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h")
            {
                return new CliOptions { ShowHelp = true };
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new FormatException($"Unexpected argument '{arg}'.");
            }

            var key = arg[2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new FormatException($"Missing value for option '{arg}'.");
            }

            options._values[key] = args[++i];
        }

        return options;
    }

    public bool TryGet(string name, out string value) => _values.TryGetValue(name, out value!);

    public string Get(string name, string defaultValue) => _values.TryGetValue(name, out var value) ? value : defaultValue;

    public int GetInt(string name, int defaultValue)
    {
        var value = Get(name, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException($"Option --{name} must be an integer.");
        }

        return parsed;
    }
}
