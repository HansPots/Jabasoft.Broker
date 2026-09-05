using System.Net.Sockets;

namespace Jabasoft.Broker;

/// <summary>
/// Builds a plain-English message for a failed HTTP connection attempt.
/// Carried over unchanged from JabaSoftLocalAiStudio's Infrastructure.LLM -
/// SocketException.Message is produced by the OS and follows the machine's
/// Windows display language, not any app's own culture settings.
/// </summary>
internal static class ConnectionErrorFormatter
{
    public static string Describe(HttpRequestException ex, string serverUrl)
    {
        if (ex.InnerException is SocketException socketEx)
        {
            var reason = socketEx.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "connection refused - is the server running?",
                SocketError.TimedOut => "connection timed out",
                SocketError.HostNotFound => "host not found",
                SocketError.NetworkUnreachable or SocketError.NetworkDown => "network unreachable",
                _ => $"socket error ({socketEx.SocketErrorCode})",
            };

            return $"Could not reach {serverUrl}: {reason}.";
        }

        return $"Could not reach {serverUrl}: {ex.Message}";
    }
}
