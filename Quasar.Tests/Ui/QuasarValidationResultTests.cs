using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarValidationResultTests
{
    private static readonly string[] PortMessage = ["Port must be between 1 and 65535."];
    private static readonly string[] TwoMessages = ["One.", "Two."];
    [Fact]
    public void DefaultIsValid()
    {
        var result = default(QuasarValidationResult);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void NullMessageIsValid()
    {
        QuasarValidationResult result = (string?)null;

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SingleMessageConverts()
    {
        QuasarValidationResult result = "Port must be between 1 and 65535.";

        Assert.False(result.IsValid);
        Assert.Equal(PortMessage, result.Errors);
    }

    [Fact]
    public void FromErrorsDropsBlankEntries()
    {
        var result = QuasarValidationResult.FromErrors("One.", " ", "", "Two.");

        Assert.False(result.IsValid);
        Assert.Equal(TwoMessages, result.Errors);
    }

    [Fact]
    public void FromErrorsWithoutMessagesIsValid()
    {
        var result = QuasarValidationResult.FromErrors();

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }
}
