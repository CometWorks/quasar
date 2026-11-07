using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CometWorks.Diagnostics;

namespace Quasar.Diagnostics;

internal static class IncidentClassification
{
    // Capture persists Exception.ToString(), not live exception objects. Match exception headers,
    // including AggregateException branches, rather than searching arbitrary log/stack text.
    private static readonly Regex ExceptionHeader = new(@"^(?<type>[\w.+`]+)(?: \((?<code>[^)]+)\))?: (?<message>.*)");
    // Each collector reads local runs. Native errno numbers differ between platforms.
    private static readonly string AddressInUseSocketCode = new SocketException((int)SocketError.AddressAlreadyInUse)
        .NativeErrorCode.ToString(CultureInfo.InvariantCulture);

    internal static bool IsAddressInUse(IncidentReport report)
    {
        if (report.Kind == "log_batch") return false;
        if (string.IsNullOrWhiteSpace(report.Exception)) return IsBindMessage(report.Message);

        Match? leaf = null;
        bool found = false;
        bool firstLine = true;
        foreach (string raw in report.Exception.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw;
            bool inner = line.StartsWith("---> ", StringComparison.Ordinal);
            if (inner) line = line[5..];
            if (line.StartsWith("(Inner Exception #", StringComparison.Ordinal))
            {
                int end = line.IndexOf(") ", StringComparison.Ordinal);
                if (end >= 0) line = line[(end + 2)..];
            }
            if (line.EndsWith("<---", StringComparison.Ordinal)) line = line[..^4].TrimEnd();
            var header = firstLine || inner ? ExceptionHeader.Match(line) : Match.Empty;
            firstLine = false;
            if (header.Success)
            {
                // Consecutive headers descend through wrappers; a stack/end marker finishes a leaf.
                leaf = header;
                continue;
            }
            if (leaf is null) continue;
            if (!IsAddressInUseException(leaf)) return false;
            found = true;
            leaf = null;
        }
        return leaf is null ? found : IsAddressInUseException(leaf);
    }

    private static bool IsAddressInUseException(Match header)
    {
        string message = header.Groups["message"].Value;
        return header.Groups["type"].Value switch
        {
            "Microsoft.AspNetCore.Connections.AddressInUseException" => true,
            "System.IO.IOException" => IsBindMessage(message),
            "System.Net.Sockets.SocketException" => header.Groups["code"].Value == AddressInUseSocketCode
                || message.StartsWith("Address already in use", StringComparison.OrdinalIgnoreCase)
                || message.StartsWith("Only one usage of each socket address (protocol/network address/port) is normally permitted", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsBindMessage(string message) =>
        message.StartsWith("Failed to bind to address ", StringComparison.Ordinal)
        && message.EndsWith(": address already in use.", StringComparison.OrdinalIgnoreCase);
}
