using System.Threading.Tasks;
using PluginSdk;
using PluginSdk.Commands;

namespace Quasar.Agent
{
    [CommandRoot("stop", "Quasar", "Save the world then shut the server down")]
    public sealed class StopCommand : CommandModule
    {
        [Command("", "Save the world then shut the server down")]
        public void Stop()
        {
            Context.Respond("Saving world and shutting the server down...");
            Task.Run(ServerControl.SaveAndQuit);
        }
    }
}
