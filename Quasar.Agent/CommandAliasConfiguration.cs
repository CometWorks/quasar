using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PluginSdk.Commands;
using PluginSdk.Config;
using PluginSdk.Tools;

namespace Quasar.Agent
{
    /// <summary>Profile-owned aliases; clusters load the same values through canonical plugin configuration.</summary>
    public sealed class CommandAliasConfiguration : PluginConfig
    {
        private bool active;

        [DictOption(64, "Command aliases from the configuration profile. Profile changes apply on restart or cluster deployment; target permissions still apply.")]
        public SerializableDictionary<string, string> Aliases
        {
            get;
            set
            {
                if (active) Apply(value);
                SetField(ref field, value);
            }
        } = new SerializableDictionary<string, string>();

        internal static CommandAliasConfiguration Load()
        {
            string root = Environment.GetEnvironmentVariable("QUASAR_MAGNETAR_APPDATA_PATH");
            string path = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "CommandAliases.json");
            // LoadJson resolves the pinned canonical envelope on cluster nodes, regardless of this fallback.
            var config = ConfigStorage.LoadJson<CommandAliasConfiguration>(!ManagedPluginConfiguration.Required && path != null && File.Exists(path) ? File.ReadAllText(path) : "{}");
            Apply(config.Aliases);
            config.active = true;
            return config;
        }

        internal static void Apply(IEnumerable<KeyValuePair<string, string>> aliases)
        {
            var method = typeof(ServerCommands).GetMethod("SetAliases", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                // Older Magnetar remains supported for profiles without aliases.
                if (aliases.Any())
                    throw new InvalidOperationException("Command aliases require a newer Magnetar with PluginSdk contract 1.1. Update Magnetar.");
                return;
            }
            try { method.Invoke(null, new object[] { aliases }); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }
    }
}
