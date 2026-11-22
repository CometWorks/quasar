namespace Quasar.Services;

/// <summary>
/// Controls how the launch hot path treats an already-installed managed Dedicated Server.
/// Full validation re-hashes the entire multi-GB install and takes minutes, so it must not
/// be the every-launch default.
/// </summary>
public enum DedicatedServerLaunchUpdateMode
{
    /// <summary>Use the installed managed DS as-is; never run SteamCMD on launch (fastest).</summary>
    Skip,

    /// <summary>Run a lightweight <c>app_update</c> check on launch, downloading only when Steam
    /// reports a newer build; no full file validation. Default.</summary>
    Update,

    /// <summary>Run <c>app_update ... validate</c> on launch, re-hashing the whole install (slowest).</summary>
    Validate,
}
