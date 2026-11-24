using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Quasar.Client.Ui.Toasts;
using Quasar.Components.Shared;
using Quasar.Ui;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarToastItemTests
{
    [Fact]
    public async Task PointerAndFocusPauseTogether()
    {
        using var context = new QuasarBunitContext();
        context.Services.AddLogging();
        var time = new FakeTimeProvider();
        var service = new QuasarToastService(time, new QuasarToastHostOptions());
        service.Add("Paused", QuasarSeverity.Normal, options => options.Duration = TimeSpan.FromSeconds(10));
        var toast = Assert.Single(service.Snapshot);
        context.Services.Replace(ServiceDescriptor.Singleton(service));
        context.Services.Replace(ServiceDescriptor.Singleton<IQuasarToastService>(service));

        var cut = context.Render<QuasarToastItem>(parameters => parameters.Add(p => p.Toast, toast));
        var element = cut.Find(".quasar-toast");

        await cut.InvokeAsync(() => element.MouseEnterAsync(new MouseEventArgs()));
        await cut.InvokeAsync(() => element.FocusInAsync(new FocusEventArgs()));
        await cut.InvokeAsync(() => element.MouseLeaveAsync(new MouseEventArgs()));

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(service.Snapshot);

        await cut.InvokeAsync(() => element.FocusOutAsync(new FocusEventArgs()));
        time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);

        Assert.Empty(service.Snapshot);
    }
}
