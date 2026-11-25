using Xunit;

namespace Quasar.Tests;

/// <summary>
/// Runs a test only on Linux. Other platforms report it as skipped rather than
/// silently passed.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "This test requires Linux.";
    }
}

/// <summary>
/// Runs a theory only on Linux. Other platforms report it as skipped rather than
/// silently passed.
/// </summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "This test requires Linux.";
    }
}
