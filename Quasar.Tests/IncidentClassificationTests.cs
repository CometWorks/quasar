using System.Globalization;
using System.Net.Sockets;
using CometWorks.Diagnostics;
using Quasar.Diagnostics;
using Xunit;

namespace Quasar.Tests;

public sealed class IncidentClassificationTests
{
    [Fact]
    public void NativeSocketCodeHandlesLocalizedMessagesWithoutConfusingPlatformErrorNumbers()
    {
        string native = new SocketException((int)SocketError.AddressAlreadyInUse).NativeErrorCode.ToString(CultureInfo.InvariantCulture);
        var report = new IncidentReport(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            "test", "log", "Error", "Hosting failed", $"System.Net.Sockets.SocketException ({native}): Localized error message", new());
        Assert.True(IncidentClassification.IsAddressInUse(report));
        string otherPlatform = native == "48" ? "98" : "48";
        Assert.False(IncidentClassification.IsAddressInUse(report with
        { Exception = $"System.Net.Sockets.SocketException ({otherPlatform}): An unrelated native error" }));
    }

    [Theory]
    [InlineData("System.Net.Sockets.SocketException (98): Address already in use", true)]
    [InlineData("System.Net.Sockets.SocketException (48): Address already in use", true)]
    [InlineData("System.Net.Sockets.SocketException (10048): Only one usage of each socket address (protocol/network address/port) is normally permitted.", true)]
    [InlineData("System.Net.Sockets.SocketException (0x80004005): Only one usage of each socket address (protocol/network address/port) is normally permitted", true)]
    [InlineData("System.Net.Sockets.SocketException (10013): Permission denied", false)]
    [InlineData("System.Net.Sockets.SocketException (99): Cannot assign requested address", false)]
    [InlineData("System.IO.IOException: Disk full", false)]
    [InlineData("System.Exception: Address already in use", false)]
    [InlineData("System.InvalidOperationException: Previous output\nSystem.Net.Sockets.SocketException (98): Address already in use", false)]
    [InlineData("System.InvalidOperationException: Failed to bind to address http://[::]:8080: address already in use.", false)]
    public void SerializedPlatformExceptionsAreClassifiedNarrowly(string exception, bool excluded)
    {
        var report = new IncidentReport(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            "test", "log", "Error", "Hosting failed to start", exception, new());
        Assert.Equal(excluded, IncidentClassification.IsAddressInUse(report));
        Assert.False(IncidentClassification.IsAddressInUse(report with { Kind = "log_batch" }));
    }

    [Theory]
    [InlineData("Failed to bind to address http://[::]:8080: address already in use.", true)]
    [InlineData("Failed to bind to address https://localhost:9090: address already in use.", true)]
    [InlineData("Failed to bind to address http://[::]:8080: permission denied.", false)]
    [InlineData("Configuration failed", false)]
    [InlineData("Previous error: Failed to bind to address http://[::]:8080: address already in use.", false)]
    public void MessageOnlyLogsRequireTheSpecificBindFailure(string message, bool excluded)
    {
        var report = new IncidentReport(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            "test", "log", "Error", message, null, new());
        Assert.Equal(excluded, IncidentClassification.IsAddressInUse(report));
        Assert.False(IncidentClassification.IsAddressInUse(report with { Kind = "log_batch" }));
    }
}
