namespace Quasar.Models;

/// <summary>
/// Selects which Magnetar build (and therefore which .NET runtime) launches a managed
/// Space Engineers dedicated server. Only meaningful on Windows, where both builds ship;
/// non-Windows hosts always run <see cref="DotNet10"/>.
/// </summary>
public enum ManagedServerRuntime
{
    /// <summary>Magnetar "Interim" build running on .NET 10. Default; the only option on Linux.</summary>
    DotNet10 = 0,

    /// <summary>Magnetar "Legacy" build running on .NET Framework 4.8. Windows only.</summary>
    NetFramework48 = 1,
}
