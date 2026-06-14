namespace LNCP.Protocol;

public static class ProtocolConstants
{
    public const string Version = "LNCP/1.0";
    public const int DefaultDiscoveryPort = 45678;
    public const int DefaultTcpPort = 5001;
    public const int DefaultDeadlineSeconds = 60;
    public const int MaxHeaderBytes = 8192;
    public const int MaxBodyBytes = 64 * 1024;

    public const string HeaderRequestId = "Request-Id";
    public const string HeaderRecipient = "Recipient";
    public const string HeaderDeadlineUtc = "Deadline-Utc";
    public const string HeaderTcpPort = "Tcp-Port";
    public const string HeaderStatus = "Status";
    public const string HeaderReason = "Reason";
    public const string HeaderLength = "Length";
}
