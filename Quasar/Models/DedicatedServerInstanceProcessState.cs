namespace Quasar.Models;

public enum DedicatedServerInstanceProcessState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Restarting = 4,
    Crashed = 5,
    Faulted = 6,
}
