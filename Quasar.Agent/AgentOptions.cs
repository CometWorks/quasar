using System;

namespace Quasar.Agent
{
    /// <summary>
    /// Connection-resilience settings for the agent's link to Quasar. Read from
    /// environment variables that Quasar sets when it launches a managed instance,
    /// or that an operator sets for a standalone server. Sensible defaults apply
    /// when nothing is configured, so an unmanaged server keeps working.
    /// </summary>
    public sealed class AgentOptions
    {
        /// <summary>
        /// How long, in seconds, to keep the server running after losing contact
        /// with Quasar before saving the world and stopping it. Zero or negative
        /// means stop promptly once Quasar is gone. The self-stop only arms after
        /// the agent has successfully connected to Quasar at least once, so a
        /// server that never reached Quasar is never auto-stopped.
        /// </summary>
        public int OfflineShutdownSeconds { get; set; } = 3600;

        /// <summary>Base delay between reconnection attempts, in seconds.</summary>
        public int ReconnectIntervalSeconds { get; set; } = 10;

        /// <summary>Random plus/minus jitter applied to each reconnect delay, in
        /// seconds, to avoid reconnect storms when many agents lose Quasar at once.</summary>
        public int ReconnectJitterSeconds { get; set; } = 3;

        public static AgentOptions FromEnvironment()
        {
            var options = new AgentOptions
            {
                // Zero/negative is meaningful here (stop promptly), so it is kept as-is.
                OfflineShutdownSeconds = ReadInt("QUASAR_AGENT_OFFLINE_SHUTDOWN_SECONDS", 3600),
                ReconnectIntervalSeconds = ReadInt("QUASAR_AGENT_RECONNECT_INTERVAL_SECONDS", 10),
                ReconnectJitterSeconds = ReadInt("QUASAR_AGENT_RECONNECT_JITTER_SECONDS", 3),
            };

            if (options.ReconnectIntervalSeconds < 1)
                options.ReconnectIntervalSeconds = 10;

            if (options.ReconnectJitterSeconds < 0)
                options.ReconnectJitterSeconds = 3;

            return options;
        }

        private static int ReadInt(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out var value)
                ? value
                : fallback;
        }
    }
}
