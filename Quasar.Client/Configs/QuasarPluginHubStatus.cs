using Quasar.Models;

namespace Quasar.Services;

public sealed record QuasarPluginHubStatus(
    QuasarPluginHub Hub,
    bool IsDefault,
    int PluginCount,
    DateTimeOffset? LastRefreshUtc,
    string Error);
