using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Services;

namespace Quasar.Services.Plugins;

public sealed class QuasarUiPluginStateStore
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _stateFilePath;
    private readonly Dictionary<string, QuasarUiPluginPackageState> _states;
    private readonly HashSet<string> _suppressedImplicitInstallCatalogIds;

    public QuasarUiPluginStateStore()
        : this(StateFilePath)
    {
    }

    internal QuasarUiPluginStateStore(string stateFilePath)
    {
        _stateFilePath = stateFilePath;
        var document = LoadDocument(stateFilePath);
        _states = CreateStateLookup(document.Plugins);
        _suppressedImplicitInstallCatalogIds = new HashSet<string>(
            document.SuppressedImplicitInstallCatalogIds.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
    }

    public event Action? Changed;

    public static string StateFilePath => Path.Combine(MagnetarPaths.GetQuasarDirectory(), "ui-plugins.state.json");

    public IReadOnlyList<QuasarUiPluginPackageState> GetStates()
    {
        lock (_sync)
        {
            return _states.Values
                .Select(Clone)
                .OrderBy(state => state.PluginId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public QuasarUiPluginPackageState GetState(string pluginId)
    {
        lock (_sync)
        {
            return _states.TryGetValue(pluginId, out var state)
                ? Clone(state)
                : new QuasarUiPluginPackageState { PluginId = pluginId, Enabled = true };
        }
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            throw new InvalidOperationException("Plugin ID is empty.");

        QuasarUiPluginPackageState state;
        lock (_sync)
        {
            state = _states.TryGetValue(pluginId, out var existing)
                ? Clone(existing)
                : new QuasarUiPluginPackageState { PluginId = pluginId };
            state.PluginId = pluginId;
            state.Enabled = enabled;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            _states[pluginId] = state;
        }

        await SaveAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task RemoveAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            return;

        var changed = false;
        lock (_sync)
            changed = _states.Remove(pluginId);

        if (!changed)
            return;

        await SaveAsync(cancellationToken);
        Changed?.Invoke();
    }

    public bool IsImplicitInstallSuppressed(string catalogId)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
            return false;

        lock (_sync)
            return _suppressedImplicitInstallCatalogIds.Contains(catalogId);
    }

    public async Task SetImplicitInstallSuppressedAsync(
        string catalogId,
        bool suppressed,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
            throw new InvalidOperationException("Plugin catalog ID is empty.");

        bool changed;
        lock (_sync)
        {
            changed = suppressed
                ? _suppressedImplicitInstallCatalogIds.Add(catalogId)
                : _suppressedImplicitInstallCatalogIds.Remove(catalogId);
        }

        if (!changed)
            return;

        await SaveAsync(cancellationToken);
        Changed?.Invoke();
    }

    public static Dictionary<string, QuasarUiPluginPackageState> LoadSnapshot() =>
        CreateStateLookup(LoadDocument(StateFilePath).Plugins);

    private static QuasarUiPluginStateDocument LoadDocument(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new QuasarUiPluginStateDocument();

            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<QuasarUiPluginStateDocument>(json, JsonOptions);
            if (document?.SchemaVersion != SchemaVersion)
                return new QuasarUiPluginStateDocument();

            return document;
        }
        catch
        {
            return new QuasarUiPluginStateDocument();
        }
    }

    private static Dictionary<string, QuasarUiPluginPackageState> CreateStateLookup(
        IEnumerable<QuasarUiPluginPackageState> states) =>
        states
            .Where(state => !string.IsNullOrWhiteSpace(state.PluginId))
            .GroupBy(state => state.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => Clone(group.Last()),
                StringComparer.OrdinalIgnoreCase);

    public static bool IsEnabled(IReadOnlyDictionary<string, QuasarUiPluginPackageState> states, string pluginId) =>
        string.IsNullOrWhiteSpace(pluginId) ||
        !states.TryGetValue(pluginId, out var state) ||
        state.Enabled;

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        List<QuasarUiPluginPackageState> states;
        List<string> suppressedImplicitInstallCatalogIds;
        lock (_sync)
        {
            states = _states.Values
                .Select(Clone)
                .OrderBy(state => state.PluginId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            suppressedImplicitInstallCatalogIds = _suppressedImplicitInstallCatalogIds
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var payload = new QuasarUiPluginStateDocument
        {
            SchemaVersion = SchemaVersion,
            Plugins = states,
            SuppressedImplicitInstallCatalogIds = suppressedImplicitInstallCatalogIds,
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(_stateFilePath, json, cancellationToken);
    }

    private static QuasarUiPluginPackageState Clone(QuasarUiPluginPackageState state) =>
        new()
        {
            PluginId = state.PluginId,
            Enabled = state.Enabled,
            UpdatedAtUtc = state.UpdatedAtUtc,
        };

    private sealed class QuasarUiPluginStateDocument
    {
        public int SchemaVersion { get; set; } = QuasarUiPluginStateStore.SchemaVersion;

        public List<QuasarUiPluginPackageState> Plugins { get; set; } = [];

        public List<string> SuppressedImplicitInstallCatalogIds { get; set; } = [];
    }
}

public sealed class QuasarUiPluginPackageState
{
    public string PluginId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
