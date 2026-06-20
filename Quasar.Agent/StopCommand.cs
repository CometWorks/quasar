using System;
using System.Threading.Tasks;
using PluginSdk;
using PluginSdk.Commands;

namespace Quasar.Agent
{
    [CommandRoot("stop", "Quasar", "Save the world then shut the server down")]
    public sealed class StopCommand : CommandModule
    {
        internal static Action AdminStopRequested { get; set; }

        [Command("", "Save the world then shut the server down")]
        public void Stop()
        {
            Context.Respond("Saving world and shutting the server down...");
            Task.Run(() =>
            {
                TryNotifyAdminStopRequested();
                ServerControl.SaveAndQuit();
            });
        }

        private static void TryNotifyAdminStopRequested()
        {
            try
            {
                AdminStopRequested?.Invoke();
            }
            catch
            {
            }
        }
    }
}
