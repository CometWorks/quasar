using System;
using System.Threading;
using System.Threading.Tasks;
using PluginSdk;
using PluginSdk.Clustering;

namespace Quasar.Agent
{
    /// <summary>
    /// Hands the restarts that plugins request through PluginSdk
    /// <see cref="ServerControl"/> (the Essentials Restart auto command step,
    /// <c>!ess restart</c>) over to Quasar. Left to Magnetar, the server
    /// restarts itself: in place on Linux, so the agent socket drops and
    /// Quasar's health policy kills and relaunches the server, and as a new
    /// detached process on Windows, which Quasar takes for a crash and starts a
    /// second server next to it. Instead, the agent reports admin-restart and
    /// quits, like its own <c>!restart</c>, and Quasar starts the server again.
    ///
    /// <para>
    /// Registered as the PluginSdk lifecycle provider in standalone mode only;
    /// a cluster registers its own. Shutdown requests, and restarts while
    /// Quasar can't be told, run in Magnetar unchanged.
    /// </para>
    /// </summary>
    internal sealed class SupervisedLifecycle : IClusterLifecycleProvider
    {
        private readonly Func<bool> _reportRestart;
        private readonly Action<ServerTerminationKind, bool> _runLocally;

        public SupervisedLifecycle(Func<bool> reportRestart, Action<ServerTerminationKind, bool> runLocally = null)
        {
            _reportRestart = reportRestart;
            _runLocally = runLocally ?? RunLocally;
        }

        public Task<ClusterLifecycleAcknowledgement> RequestAsync(
            ClusterLifecycleRequest request, CancellationToken cancellationToken)
        {
            var kind = request.Kind;
            if (kind == ServerTerminationKind.Restart && _reportRestart())
            {
                Console.WriteLine("[Quasar.Agent] Restart requested through ServerControl; quitting so Quasar starts the server again.");
                kind = ServerTerminationKind.Shutdown;
            }

            // ServerControl would route the call back here while registered.
            // The call does not return when the process exits.
            ClusterLifecycle.Unregister(this);
            try
            {
                _runLocally(kind, request.SaveFirst);
            }
            finally
            {
                ClusterLifecycle.Register(this);
            }

            return Task.FromResult(new ClusterLifecycleAcknowledgement(
                request.RequestId, ClusterLifecycleDisposition.Accepted, Guid.Empty, string.Empty, string.Empty));
        }

        private static void RunLocally(ServerTerminationKind kind, bool saveFirst)
        {
            if (kind == ServerTerminationKind.Restart)
            {
                if (saveFirst)
                    ServerControl.SaveAndRestart();
                else
                    ServerControl.RestartWithoutSaving();
            }
            else if (saveFirst)
            {
                ServerControl.SaveAndQuit();
            }
            else
            {
                ServerControl.QuitWithoutSaving();
            }
        }
    }
}
