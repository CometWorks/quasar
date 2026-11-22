namespace Quasar.Client.Plugins;

public sealed record BrowserExtension(string TargetKey, string PluginId, string Mode, int Priority,
    string Component, string AssemblyName, string Digest);

public sealed record BrowserPluginAssembly(string PluginId, string AssemblyName, string Digest);
