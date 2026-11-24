using Microsoft.AspNetCore.Components;
using Quasar.Client.Ui.Dialogs;
using Quasar.Ui;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarDialogServiceTests
{
    [Fact]
    public async Task ShowAddsEntryAndCloseCompletesResultOnce()
    {
        using var service = new QuasarDialogService();
        var reference = await service.ShowAsync<TestDialog>("Test dialog");

        Assert.Single(service.Entries);
        Assert.Equal("Test dialog", service.Entries[0].Title);

        reference.Close(QuasarDialogResult.Ok("confirmed"));
        Assert.True(service.Entries[0].CloseRequested);
        Assert.Equal("confirmed", service.Entries[0].RequestedResult?.Data);

        service.FinishClose(reference.Id);
        var result = await reference.Result;
        Assert.False(result.Canceled);
        Assert.Equal("confirmed", result.Data);
        Assert.Empty(service.Entries);

        service.FinishClose(reference.Id);
        Assert.True((await reference.Result).Canceled == false);
    }

    [Fact]
    public async Task ShowReturnsReferenceAndEntryImmediately()
    {
        using var service = new QuasarDialogService();
        var reference = service.Show<TestDialog>("Sync dialog");

        Assert.Single(service.Entries);
        Assert.Equal("Sync dialog", service.Entries[0].Title);

        reference.Close(QuasarDialogResult.Ok("sync"));
        service.FinishClose(reference.Id);

        var result = await reference.Result;
        Assert.False(result.Canceled);
        Assert.Equal("sync", result.Data);
    }

    [Fact]
    public void ShowRejectsDisposedService()
    {
        var service = new QuasarDialogService();
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.Show<TestDialog>());
    }

    [Fact]
    public async Task ShowAddsOneEntryPerDialog()
    {
        using var service = new QuasarDialogService();
        var first = await service.ShowAsync<TestDialog>("First");
        var second = await service.ShowAsync<TestDialog>("Second");

        Assert.Equal(2, service.Entries.Count);
        Assert.Contains(service.Entries, entry => entry.Id == first.Id);
        Assert.Contains(service.Entries, entry => entry.Id == second.Id);
    }

    [Fact]
    public async Task CanceledTokenReturnsCanceledReference()
    {
        using var service = new QuasarDialogService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var reference = await service.ShowAsync<TestDialog>(cancellationToken: cancellation.Token);

        Assert.True((await reference.Result).Canceled);
        Assert.Empty(service.Entries);
    }

    [Fact]
    public async Task DisposeCompletesOpenDialogsAsCanceled()
    {
        var service = new QuasarDialogService();
        var reference = await service.ShowAsync<TestDialog>();

        service.Dispose();

        Assert.True((await reference.Result).Canceled);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ShowAsync<TestDialog>());
    }

    [Fact]
    public async Task NavigationClosesOnlyDialogsThatAllowIt()
    {
        using var service = new QuasarDialogService();
        var closable = await service.ShowAsync<TestDialog>();
        var locked = await service.ShowAsync<TestDialog>(options: new QuasarDialogOptions { CloseOnNavigation = false });

        service.RequestCloseOnNavigation();

        Assert.True(service.Entries.Single(entry => entry.Id == closable.Id).CloseRequested);
        Assert.False(service.Entries.Single(entry => entry.Id == locked.Id).CloseRequested);
    }

    [Fact]
    public async Task ModelessOptionsDefaultToMovableAndResizable()
    {
        using var service = new QuasarDialogService();
        var reference = await service.ShowAsync<TestDialog>(
            options: new QuasarDialogOptions { Modal = false });

        var entry = Assert.Single(service.Entries);
        Assert.True(entry.Options.Draggable);
        Assert.True(entry.Options.Resizable);

        service.FinishClose(reference.Id);
        Assert.True((await reference.Result).Canceled);
    }

    [Fact]
    public async Task GenericAndTypeOverloadsRenderTheRequestedComponent()
    {
        using var service = new QuasarDialogService();
        var reference = await service.ShowAsync(typeof(TestDialog));

        var entry = Assert.Single(service.Entries);
        Assert.Equal(typeof(TestDialog), entry.ComponentType);
        Assert.Same(reference, entry.Reference);

        service.FinishClose(reference.Id);
        Assert.True((await reference.Result).Canceled);
    }

    private sealed class TestDialog : IComponent
    {
        public void Attach(RenderHandle renderHandle)
        {
        }

        public Task SetParametersAsync(ParameterView parameters) => Task.CompletedTask;
    }
}
