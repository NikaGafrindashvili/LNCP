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

if (!options.TryGet("recipient", out var recipient))
{
    Console.Error.WriteLine("Missing required option --recipient.");
    PrintUsage();
    return 2;
}

try
{
    DiscoveryMessage.ValidateNickname(recipient);

    var tcpPort = options.GetInt("tcp-port", ProtocolConstants.DefaultTcpPort);
    var discoveryPort = options.GetInt("discovery-port", ProtocolConstants.DefaultDiscoveryPort);
    var deadlineSeconds = options.GetInt("deadline-seconds", ProtocolConstants.DefaultDeadlineSeconds);
    var broadcastAddress = IPAddress.Parse(options.Get("broadcast-address", "255.255.255.255"));
    var requestId = Guid.NewGuid();
    var deadline = DateTimeOffset.UtcNow.AddSeconds(deadlineSeconds);

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
    deadlineCts.CancelAfter(TimeSpan.FromSeconds(deadlineSeconds));

    var listener = new TcpListener(IPAddress.Any, tcpPort);
    listener.Start();

    try
    {
        Console.WriteLine($"LNCP initiator request {requestId}");
        Console.WriteLine($"Listening on TCP port {tcpPort}; deadline {deadline:O}");

        await SendDiscoveryAsync(requestId, recipient, deadline, tcpPort, broadcastAddress, discoveryPort, deadlineCts.Token);
        Console.WriteLine($"Broadcast DISCOVER for recipient '{recipient}' on UDP port {discoveryPort}.");
        Console.WriteLine("Waiting for recipient TCP connection...");

        using var client = await listener.AcceptTcpClientAsync(deadlineCts.Token);
        client.NoDelay = true;
        Console.WriteLine($"TCP connection from {client.Client.RemoteEndPoint}.");

        await HandleHandshakeAndChatAsync(client, requestId, deadline, deadlineCts.Token);
        return 0;
    }
    finally
    {
        listener.Stop();
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Request cancelled or deadline expired.");
    return 1;
}
catch (Exception ex) when (ex is ProtocolException or SocketException or FormatException or IOException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static async Task SendDiscoveryAsync(Guid requestId, string recipient, DateTimeOffset deadline, int tcpPort, IPAddress broadcastAddress, int discoveryPort, CancellationToken cancellationToken)
{
    using var udp = new UdpClient();
    udp.EnableBroadcast = true;

    var discovery = new DiscoveryMessage(requestId, recipient, deadline, tcpPort);
    var bytes = LncpFrameWriter.SerializeDatagram(discovery.ToMessage());
    await udp.SendAsync(bytes, new IPEndPoint(broadcastAddress, discoveryPort), cancellationToken);
}

static async Task HandleHandshakeAndChatAsync(TcpClient client, Guid expectedRequestId, DateTimeOffset deadline, CancellationToken cancellationToken)
{
    await using var stream = client.GetStream();
    var accepted = false;
    var reason = string.Empty;

    try
    {
        var hello = await LncpFrameReader.ReadAsync(stream, cancellationToken);
        if (hello.Type != LncpMessageType.Hello)
        {
            reason = "Expected HELLO handshake message.";
        }
        else
        {
            var requestId = DiscoveryMessage.ParseGuid(hello.RequiredHeader(ProtocolConstants.HeaderRequestId));
            if (requestId != expectedRequestId)
            {
                reason = "Request-Id does not match an active request.";
            }
            else if (DateTimeOffset.UtcNow > deadline)
            {
                reason = "Request deadline has expired.";
            }
            else
            {
                accepted = true;
            }
        }
    }
    catch (ProtocolException ex)
    {
        reason = ex.Message;
    }

    await LncpFrameWriter.WriteAsync(
        stream,
        new LncpMessage(
            LncpMessageType.HandshakeResponse,
            new Dictionary<string, string>
            {
                [ProtocolConstants.HeaderStatus] = accepted ? "ACCEPTED" : "REJECTED",
                [ProtocolConstants.HeaderReason] = accepted ? "OK" : reason
            }),
        cancellationToken);

    if (!accepted)
    {
        Console.WriteLine($"Rejected connection: {reason}");
        return;
    }

    Console.WriteLine("Handshake accepted. Type /quit to close.");
    await ChatAsSenderFirstAsync(stream, cancellationToken);
}

static async Task ChatAsSenderFirstAsync(Stream stream, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        Console.Write("you> ");
        var outbound = Console.ReadLine();
        if (outbound is null || outbound.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            await SendCloseAsync(stream, "User requested close.", cancellationToken);
            Console.WriteLine("Connection closed.");
            return;
        }

        await LncpFrameWriter.WriteAsync(stream, new LncpMessage(LncpMessageType.Text, body: outbound), cancellationToken);

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
    Console.WriteLine("  dotnet run --project LNCP.Initiator -- --recipient <nickname> [--tcp-port 5001] [--deadline-seconds 60] [--discovery-port 45678] [--broadcast-address 255.255.255.255]");
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
