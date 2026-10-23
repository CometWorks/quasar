using System;
using System.Runtime.CompilerServices;
using PluginSdk.Clustering;

namespace Quasar.Agent
{
    // A hot spare hosts no partitions, so its metrics, profiler and plugin stats are noise in
    // Quasar's analytics. The spare role rotates over the nodes, so Quasar cannot filter them out
    // by node; the agent pauses the telemetry itself until the node owns a partition again.
    internal static class ClusterTelemetryGate
    {
        private const string WorldAuthorityRole = "WorldAuthority";

        public static bool IsPaused(AgentOptions options)
        {
            if (!options.ClusterMode)
                return false;

            try
            {
                return IsIdle();
            }
            catch (Exception)
            {
                // A Magnetar without the cluster SDK keeps the old behavior: always send.
                return false;
            }
        }

        // Kept out of IsPaused so a missing PluginSdk.Clustering fails here, inside the try.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsIdle() => IsIdle(PluginCluster.Current?.Context);

        // The World Authority owns no partitions but is never a spare. Before the cluster
        // provider registers, the node is still starting and owns nothing yet.
        internal static bool IsIdle(NodeContext context) =>
            context == null
            || (context.Role != WorldAuthorityRole && (context.OwnedPartitions?.Count ?? 0) == 0);
    }
}
