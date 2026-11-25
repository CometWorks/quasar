using System.Linq.Expressions;
using Quasar.Ui;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarDialogParametersTests
{
    [Fact]
    public void GenericParametersExtractMemberNames()
    {
        var parameters = new QuasarDialogParameters<TestDialog>
        {
            { dialog => dialog.Message, "hello" },
            { dialog => dialog.Count, 3 },
        };

        Assert.Equal("hello", parameters[nameof(TestDialog.Message)]);
        Assert.Equal(3, parameters[nameof(TestDialog.Count)]);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void ParameterNamesAreCaseInsensitive()
    {
        var parameters = new QuasarDialogParameters().Set("Message", "hello");

        Assert.True(parameters.ContainsKey("message"));
        Assert.Equal("hello", parameters["MESSAGE"]);
    }

    [Fact]
    public void InvalidSelectorIsRejected()
    {
        Expression<Func<TestDialog, object?>> selector = dialog => new object();

        Assert.Throws<ArgumentException>(() => new QuasarDialogParameters<TestDialog>().Add(selector, "value"));
    }

    [Fact]
    public void NestedSelectorIsRejected()
    {
        Expression<Func<TestDialog, object?>> selector = dialog => dialog.Model.Name;

        Assert.Throws<ArgumentException>(() => new QuasarDialogParameters<TestDialog>().Add(selector, "value"));
    }

    [Fact]
    public void EmptyParameterNameIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new QuasarDialogParameters().Set(" ", "value"));
    }

    [Fact]
    public void ResultReadsTypedPayload()
    {
        var result = QuasarDialogResult.Ok("confirmed");

        Assert.True(result.TryGetData<string>(out var value));
        Assert.Equal("confirmed", value);
        Assert.False(result.TryGetData<int>(out _));
        Assert.True(QuasarDialogResult.Cancel().Canceled);
    }

    private sealed class TestDialog
    {
        public string? Message { get; set; }
        public int Count { get; set; }
        public NestedModel Model { get; } = new();
    }

    private sealed class NestedModel
    {
        public string? Name { get; set; }
    }
}
