using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Quasar.Services.Auth;

// The protected ticket carries identity and scope. Only the presence of its random
// login marker lives on disk, so a worker restart preserves both sign-in and sign-out.
internal static class DashboardLogin
{
    internal static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(12);
    private const string LoginKey = "quasar:login";
    private const string ScopeKey = "quasar:panel";
    private static string DirectoryPath => Path.Combine(MagnetarPaths.GetQuasarDirectory(), "BrowserLogins");

    internal static void Issue(CookieSigningInContext context)
    {
        var directory = OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(DirectoryPath)
            : Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var now = DateTime.UtcNow;
        // Active tickets touch their marker at least once per ticket lifetime.
        foreach (var file in directory.EnumerateFiles("*.login"))
            if (file.LastWriteTimeUtc < now - TicketLifetime - TicketLifetime)
                file.Delete();

        var id = Guid.NewGuid().ToString("N");
        using var marker = new FileStream(MarkerPath(id), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        context.Properties.Items[LoginKey] = id;
        context.Properties.Items[ScopeKey] = context.Request.PathBase.Value ?? "";
    }

    internal static string? Id(AuthenticationProperties? properties, PathString scope)
    {
        if (properties is null || properties.GetString(ScopeKey) != (scope.Value ?? "")) return null;
        var id = properties.GetString(LoginKey);
        return id is { Length: 32 } && Guid.TryParseExact(id, "N", out _) ? id : null;
    }

    internal static bool Validate(CookieValidatePrincipalContext context)
    {
        var id = Id(context.Properties, context.Request.PathBase);
        if (id is null) return false;
        var path = MarkerPath(id);
        DateTime now = DateTime.UtcNow;
        try
        {
            if (File.GetLastWriteTimeUtc(path) < now - TimeSpan.FromHours(1))
            {
                // Open existing only. A renewal racing with logout must never recreate a marker.
                using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                File.SetLastWriteTimeUtc(file, now);
            }
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static bool IsActive(string id) => File.Exists(MarkerPath(id));

    internal static void Revoke(string id)
    {
        try { File.Delete(MarkerPath(id)); }
        catch (DirectoryNotFoundException) { /* Removing the directory already ended its logins. */ }
    }

    private static string MarkerPath(string id) => Path.Combine(DirectoryPath, id + ".login");
}
