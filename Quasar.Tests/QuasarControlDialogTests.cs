using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Layout;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarControlDialogTests
{
    [Theory]
    [InlineData("Shutdown Quasar", QuasarControlAction.ShutdownQuasar)]
    [InlineData("Restart Quasar", QuasarControlAction.RestartQuasar)]
    public async Task ConfirmReturnsSelectedAction(string buttonText, QuasarControlAction action)
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var provider = context.Render<MudDialogProvider>();
        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () => dialog = await context.Services
            .GetRequiredService<IDialogService>().ShowAsync<QuasarControlDialog>("Quasar power",
                new DialogParameters { [nameof(QuasarControlDialog.IsRestartAvailable)] = true }));

        provider.FindAll("button").Single(button => button.TextContent.Contains(buttonText)).Click();
        provider.FindAll("button").Single(button => button.TextContent.Contains(buttonText)).Click();

        var result = await dialog.Result.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(result);
        Assert.False(result.Canceled);
        Assert.Equal(action, result.Data);
    }
}
