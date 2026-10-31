using System;
using System.Text.RegularExpressions;

namespace Quasar.Networking;

internal static class QuasarControlAddress
{
    public static Uri Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || value.IndexOf('\\') >= 0 || value.IndexOf('%') >= 0
            || value.IndexOf("/../", StringComparison.Ordinal) >= 0 || value.IndexOf("/./", StringComparison.Ordinal) >= 0
            || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Use an HTTPS Quasar control address. HTTP is permitted only for loopback setup.");
        if (uri.AbsolutePath == "/") return uri;
        var path = uri.AbsolutePath.Trim('/');
        if (uri.Scheme != "https" || uri.Host != "dash.se-servers.net" || !uri.IsDefaultPort
            || path.Length is < 3 or > 63 || !Regex.IsMatch(path, "\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z")
            || uri.AbsolutePath != "/" + path + "/")
            throw new ArgumentException("Use a root Quasar control address or its exact enrolled Nebula panel URL, including the trailing slash.");
        return uri;
    }
    public static Uri Endpoint(Uri address, string relativePath) => new(address, relativePath.TrimStart('/'));
}
