using System;
using System.Threading.Tasks;
using PluginSdk;
using PluginSdk.Commands;
using VRageMath;

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

    [CommandRoot("restart", "Quasar", "Save the world then restart the server")]
    public sealed class RestartCommand : CommandModule
    {
        private const int DefaultRestartDelaySeconds = 10;
        private const int MaximumRestartDelaySeconds = 3600;

        private static readonly object ScheduledRestartSync = new object();
        private static ScheduledRestart _scheduledRestart;

        internal static Action AdminRestartRequested { get; set; }

        [Command("", "Save the world then restart the server", "Usage: !restart [seconds]. Defaults to 10 seconds.")]
        public CommandReply Restart(int seconds = DefaultRestartDelaySeconds)
        {
            if (seconds < 0)
                return CommandReply.Error("Restart delay must be zero or more seconds.");

            if (seconds > MaximumRestartDelaySeconds)
                return CommandReply.Error($"Restart delay cannot exceed {MaximumRestartDelaySeconds} seconds.");

            var context = Context;
            var now = DateTime.UtcNow;
            lock (ScheduledRestartSync)
            {
                if (_scheduledRestart != null)
                    return CommandReply.Error("Restart already scheduled.");

                _scheduledRestart = new ScheduledRestart(context, now.AddSeconds(seconds), seconds);
            }

            return CommandReply.Announce(FormatRestartScheduledMessage(seconds), Color.Yellow);
        }

        internal static void UpdateScheduledRestart()
        {
            ScheduledRestart restart = null;
            string announcement = null;
            var beginRestart = false;

            lock (ScheduledRestartSync)
            {
                if (_scheduledRestart == null)
                    return;

                var remainingSeconds = (int)Math.Ceiling((_scheduledRestart.RestartAtUtc - DateTime.UtcNow).TotalSeconds);
                if (remainingSeconds <= 0)
                {
                    restart = _scheduledRestart;
                    _scheduledRestart = null;
                    beginRestart = true;
                }
                else if (remainingSeconds != _scheduledRestart.LastAnnouncedSeconds &&
                         ShouldAnnounceCountdown(remainingSeconds))
                {
                    _scheduledRestart.LastAnnouncedSeconds = remainingSeconds;
                    restart = _scheduledRestart;
                    announcement = FormatCountdownMessage(remainingSeconds);
                }
            }

            if (announcement != null)
                SendAnnouncement(restart.Context, announcement);

            if (beginRestart)
            {
                SendAnnouncement(restart.Context, "Saving world and restarting the server...");
                TryNotifyAdminRestartRequested();
                ServerControl.SaveAndQuit();
            }
        }

        internal static void ClearScheduledRestart()
        {
            lock (ScheduledRestartSync)
            {
                _scheduledRestart = null;
            }
        }

        private static void SendAnnouncement(CommandContext context, string message)
        {
            try
            {
                context.Respond(CommandReply.Announce(message, Color.Yellow));
            }
            catch
            {
            }
        }

        private static void TryNotifyAdminRestartRequested()
        {
            try
            {
                AdminRestartRequested?.Invoke();
            }
            catch
            {
            }
        }

        private static string FormatRestartScheduledMessage(int seconds)
        {
            return seconds == 0
                ? "Server restart starting now."
                : $"Server restart scheduled in {FormatSeconds(seconds)}.";
        }

        private static bool ShouldAnnounceCountdown(int seconds)
        {
            if (seconds <= 10)
                return true;

            if (seconds <= 60)
                return seconds % 15 == 0;

            return seconds % 60 == 0;
        }

        private static string FormatCountdownMessage(int seconds)
        {
            return seconds == 1
                ? "Server restart in 1 second."
                : $"Server restart in {seconds} seconds.";
        }

        private static string FormatSeconds(int seconds)
        {
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        private sealed class ScheduledRestart
        {
            public ScheduledRestart(CommandContext context, DateTime restartAtUtc, int lastAnnouncedSeconds)
            {
                Context = context;
                RestartAtUtc = restartAtUtc;
                LastAnnouncedSeconds = lastAnnouncedSeconds;
            }

            public CommandContext Context { get; }

            public DateTime RestartAtUtc { get; }

            public int LastAnnouncedSeconds { get; set; }
        }
    }

    [CommandRoot("quit", "Quasar", "Quit the server immediately without saving")]
    public sealed class QuitCommand : CommandModule
    {
        internal static Action AdminStopRequested { get; set; }

        [Command("", "Quit the server immediately without saving")]
        public void Quit()
        {
            Context.Respond("Quitting without saving...");
            Task.Run(() =>
            {
                TryNotifyAdminStopRequested();
                ServerControl.QuitWithoutSaving();
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
