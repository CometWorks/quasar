using System;
using System.Collections.Generic;
using Magnetar.Protocol.Model;
using PluginSdk.Logging;
using Sdk = PluginSdk.Stats;

namespace Quasar.Agent
{
    // Reads the in-process PluginSdk statistics registry (PluginSdk.Stats.PluginStats) and maps
    // every provider's latest self-describing snapshot onto the protocol POCOs, so the agent can
    // forward plugin telemetry to Quasar without Quasar ever referencing the PluginSdk.
    //
    // Best-effort: any failure yields null (logged once) and never disrupts the agent snapshot.
    // Runs on the game thread from GameBridge.BuildSnapshot, the same thread plugins publish on,
    // so it only reads PluginStats' own thread-safe ConcurrentDictionary.
    internal static class PluginStatsCollector
    {
        private static readonly Logger Log = Logger.Create("PluginStatsCollector");
        private static bool _loggedError;

        public static PluginStatsSnapshot Collect()
        {
            try
            {
                var providers = Sdk.PluginStats.Providers;
                if (providers == null || providers.Count == 0)
                    return null;

                var result = new List<PluginStatsProvider>(providers.Count);
                foreach (var name in providers)
                {
                    if (string.IsNullOrEmpty(name) || !Sdk.PluginStats.TryGetSnapshot(name, out var snapshot) || snapshot == null)
                        continue;

                    var groups = MapGroups(snapshot.Groups);
                    if (groups.Count == 0)
                        continue;

                    result.Add(new PluginStatsProvider
                    {
                        Provider = name,
                        // The plugin sets UtcTimestamp from DateTime.UtcNow; force UTC kind so the
                        // offset is zero even if a producer left it Unspecified.
                        CapturedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(snapshot.UtcTimestamp, DateTimeKind.Utc)),
                        Groups = groups,
                    });
                }

                return result.Count == 0 ? null : new PluginStatsSnapshot { Providers = result };
            }
            catch (Exception ex)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    Log.Error("Failed to collect plugin statistics", ex);
                }

                return null;
            }
        }

        private static List<PluginStatGroup> MapGroups(List<Sdk.StatGroup> source)
        {
            var groups = new List<PluginStatGroup>();
            if (source == null)
                return groups;

            foreach (var group in source)
            {
                if (group?.Schema == null)
                    continue;

                var fields = new List<PluginStatField>();
                if (group.Schema.Fields != null)
                {
                    foreach (var field in group.Schema.Fields)
                    {
                        if (field == null)
                            continue;

                        fields.Add(new PluginStatField
                        {
                            Name = field.Name ?? string.Empty,
                            Kind = field.Kind ?? string.Empty,
                            Description = field.Description ?? string.Empty,
                            Unit = field.Unit ?? string.Empty,
                            Parent = field.Parent ?? string.Empty,
                            AcrossInstances = field.AcrossInstances ?? string.Empty,
                            OverTime = field.OverTime ?? string.Empty,
                        });
                    }
                }

                var instances = new List<PluginStatInstance>();
                if (group.Instances != null)
                {
                    foreach (var instance in group.Instances)
                    {
                        if (instance == null)
                            continue;

                        instances.Add(new PluginStatInstance
                        {
                            Label = instance.Label,
                            Values = instance.Values ?? Array.Empty<double>(),
                        });
                    }
                }

                groups.Add(new PluginStatGroup
                {
                    Name = group.Schema.Name ?? string.Empty,
                    LabelDescription = group.Schema.LabelDescription ?? string.Empty,
                    Fields = fields,
                    Instances = instances,
                });
            }

            return groups;
        }
    }
}
