namespace Quasar.Tests.Ui;

/// <summary>
/// Polls for a condition that changes without a render, such as a debounced
/// callback that only raises an event. bUnit's wait helpers re-check on renders.
/// </summary>
internal static class Eventually
{
    public static void Until(Func<bool> condition, string message, int timeoutMilliseconds = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new Xunit.Sdk.XunitException("Timed out waiting for: " + message);

            Thread.Sleep(20);
        }
    }
}
