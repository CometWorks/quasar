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
        service.HostMounted();
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
    public async Task ShowAsyncParityCancelsAnOpenDialog()
    {
        using var service = new QuasarDialogService();
        service.HostMounted();
        using var cancellation = new CancellationTokenSource();
        var reference = await service.ShowAsync<TestDialog>(cancellationToken: cancellation.Token);
        var entry = Assert.Single(service.Entries);

        await cancellation.CancelAsync();

        Assert.True(entry.CloseRequested);
        Assert.True(entry.RequestedResult?.Canceled);

        service.FinishClose(reference.Id);
        var result = await reference.Result;
        Assert.True(result.Canceled);
        Assert.Empty(service.Entries);
    }

    [Fact]
    public async Task ShowCancelsAnOpenDialogToo()
    {
        using var service = new QuasarDialogService();
        service.HostMounted();
        using var cancellation = new CancellationTokenSource();
        var reference = service.Show<TestDialog>(cancellationToken: cancellation.Token);
        var entry = Assert.Single(service.Entries);

        await cancellation.CancelAsync();

        Assert.True(entry.CloseRequested);
        service.FinishClose(reference.Id);
        Assert.True((await reference.Result).Canceled);
    }

    [Fact]
    public async Task CancelingAfterCloseIsIgnored()
    {
        using var service = new QuasarDialogService();
        using var cancellation = new CancellationTokenSource();
        var reference = await service.ShowAsync<TestDialog>(cancellationToken: cancellation.Token);

        service.FinishClose(reference.Id);
        await cancellation.CancelAsync();

        Assert.Empty(service.Entries);
        Assert.True((await reference.Result).Canceled);
    }

    [Fact]
    public async Task DisposedTokenSourceStillOpensTheDialog()
    {
        using var service = new QuasarDialogService();
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        cancellation.Dispose();

        var reference = await service.ShowAsync<TestDialog>(cancellationToken: token);
        Assert.Single(service.Entries);

        service.FinishClose(reference.Id);
        Assert.True((await reference.Result).Canceled);
    }

    [Fact]
    public async Task DisposingTheTokenSourceWhileOpenStillCloses()
    {
        using var service = new QuasarDialogService();
        var cancellation = new CancellationTokenSource();
        var reference = await service.ShowAsync<TestDialog>(cancellationToken: cancellation.Token);
        Assert.Single(service.Entries);

        cancellation.Dispose();
        service.FinishClose(reference.Id);

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

    [Fact]
    public async Task MessageBoxYesReturnsTrueAndPassesParameters()
    {
        using var service = new QuasarDialogService();
        var task = service.ShowMessageBoxAsync("Delete backup", "Permanently delete it?", "Delete", "Keep", "Cancel");

        var entry = Assert.Single(service.Entries);
        Assert.Equal("Delete backup", entry.Title);
        Assert.Equal("Permanently delete it?", entry.Parameters["Message"]);
        Assert.Equal("Delete", entry.Parameters["YesText"]);
        Assert.Equal("Keep", entry.Parameters["NoText"]);
        Assert.Equal("Cancel", entry.Parameters["CancelText"]);

        service.RequestClose(entry, QuasarDialogResult.Ok(true));
        service.FinishClose(entry.Id);

        Assert.True((await task) == true);
    }

    [Fact]
    public async Task MessageBoxNoReturnsFalse()
    {
        using var service = new QuasarDialogService();
        var task = service.ShowMessageBoxAsync("Question", "Choose", "Yes", "No", "Cancel");

        var entry = Assert.Single(service.Entries);
        service.RequestClose(entry, QuasarDialogResult.Ok(false));
        service.FinishClose(entry.Id);

        Assert.Equal(false, await task);
    }

    [Fact]
    public async Task MessageBoxCancelReturnsNull()
    {
        using var service = new QuasarDialogService();
        var task = service.ShowMessageBoxAsync("Question", "Choose", "Yes", "No", "Cancel");

        var entry = Assert.Single(service.Entries);
        service.RequestClose(entry, QuasarDialogResult.Cancel());
        service.FinishClose(entry.Id);

        Assert.Null(await task);
    }

    [Fact]
    public async Task MessageBoxCanceledTokenReturnsNull()
    {
        using var service = new QuasarDialogService();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await service.ShowMessageBoxAsync("Question", "Choose", cancellationToken: cancellation.Token);

        Assert.Null(result);
        Assert.Empty(service.Entries);
    }

    [Fact]
    public async Task MessageBoxCanceledTokenClosesOpenBox()
    {
        using var service = new QuasarDialogService();
        service.HostMounted();
        using var cancellation = new CancellationTokenSource();
        var task = service.ShowMessageBoxAsync("Question", "Choose", cancellationToken: cancellation.Token);

        var entry = Assert.Single(service.Entries);
        await cancellation.CancelAsync();

        Assert.True(entry.CloseRequested);
        service.FinishClose(entry.Id);
        Assert.Null(await task);
        Assert.Empty(service.Entries);
    }

    [Fact]
    public async Task MessageBoxCanceledTokenWithoutHostCompletesImmediately()
    {
        using var service = new QuasarDialogService();
        using var cancellation = new CancellationTokenSource();
        var task = service.ShowMessageBoxAsync("Question", "Choose", cancellationToken: cancellation.Token);

        Assert.Single(service.Entries);
        await cancellation.CancelAsync();

        Assert.Empty(service.Entries);
        Assert.Null(await task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task UnmountingTheHostCancelsOpenDialogs()
    {
        using var service = new QuasarDialogService();
        service.HostMounted();
        var task = service.ShowMessageBoxAsync("Question", "Choose");

        Assert.Single(service.Entries);
        service.HostUnmounted();

        Assert.Empty(service.Entries);
        Assert.Null(await task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task UnmountingTheHostPreservesARecordedChoice()
    {
        using var service = new QuasarDialogService();
        service.HostMounted();
        var task = service.ShowMessageBoxAsync("Question", "Choose");

        var entry = Assert.Single(service.Entries);
        service.RequestClose(entry, QuasarDialogResult.Ok(true));
        service.HostUnmounted();

        Assert.True(await task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task MessageBoxCancellationAfterConfirmationReturnsTrue()
    {
        using var service = new QuasarDialogService();
        using var cancellation = new CancellationTokenSource();
        var task = service.ShowMessageBoxAsync("Question", "Choose", cancellationToken: cancellation.Token);

        var entry = Assert.Single(service.Entries);
        service.RequestClose(entry, QuasarDialogResult.Ok(true));
        await cancellation.CancelAsync();

        service.FinishClose(entry.Id);
        Assert.True(await task);
        Assert.Empty(service.Entries);
    }

    [Fact]
    public async Task MessageBoxRequiresNonBlankTitle()
    {
        using var service = new QuasarDialogService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ShowMessageBoxAsync(" ", "Choose"));
    }

    [Fact]
    public async Task MessageBoxNormalizesOptionalLabelsAndPassesOptions()
    {
        using var service = new QuasarDialogService();
        var task = service.ShowMessageBoxAsync("Question", "Choose",
            yesText: "  ", noText: " ", cancelText: "  ",
            options: new QuasarDialogOptions { MaxWidth = QuasarDialogWidth.Medium });

        var entry = Assert.Single(service.Entries);
        Assert.Equal("OK", entry.Parameters["YesText"]);
        Assert.Null(entry.Parameters["NoText"]);
        Assert.Null(entry.Parameters["CancelText"]);
        Assert.Equal(QuasarDialogWidth.Medium, entry.Options.MaxWidth);

        service.RequestClose(entry, QuasarDialogResult.Cancel());
        service.FinishClose(entry.Id);
        Assert.Null(await task);
    }

    [Fact]
    public async Task CloseRecordedWithoutHostIsCompletedWhenTokenIsCancelled()
    {
        using var service = new QuasarDialogService();
        using var cancellation = new CancellationTokenSource();
        var reference = service.Show<TestDialog>(cancellationToken: cancellation.Token);
        var entry = Assert.Single(service.Entries);

        service.RequestClose(entry, QuasarDialogResult.Ok("recorded"));
        await cancellation.CancelAsync();

        Assert.Empty(service.Entries);
        var result = await reference.Result.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Canceled);
        Assert.Equal("recorded", result.Data);
    }

    [Fact]
    public async Task CloseWithoutHostCompletesImmediately()
    {
        using var service = new QuasarDialogService();
        var reference = service.Show<TestDialog>();

        reference.Close(QuasarDialogResult.Ok("closed"));

        Assert.Empty(service.Entries);
        var result = await reference.Result.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Canceled);
        Assert.Equal("closed", result.Data);
    }

    [Fact]
    public async Task CancelWithoutHostCompletesImmediately()
    {
        using var service = new QuasarDialogService();
        var reference = service.Show<TestDialog>();

        reference.Cancel();

        Assert.Empty(service.Entries);
        Assert.True((await reference.Result.WaitAsync(TimeSpan.FromSeconds(2))).Canceled);
    }

    [Fact]
    public void HostMountedAfterDisposeIsIgnored()
    {
        var service = new QuasarDialogService();
        service.Dispose();

        service.HostMounted();
    }

    [Fact]
    public async Task MessageBoxRejectsNullMessage()
    {
        using var service = new QuasarDialogService();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.ShowMessageBoxAsync("Question", null!));
    }

    private sealed class TestDialog : IComponent
    {
        public void Attach(RenderHandle renderHandle)
        {
        }

        public Task SetParametersAsync(ParameterView parameters) => Task.CompletedTask;
    }
}
