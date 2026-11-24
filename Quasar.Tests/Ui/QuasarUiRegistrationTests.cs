using Microsoft.Extensions.DependencyInjection;
using Quasar.Client.Ui;
using Quasar.Ui;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarUiRegistrationTests
{
    [Fact]
    public void ConfiguredHostOptionsAreCopied()
    {
        QuasarToastHostOptions? captured = null;
        var services = new ServiceCollection();
        services.AddQuasarUi(options =>
        {
            captured = options;
            options.Position = QuasarToastPosition.BottomLeft;
            options.MaxVisibleToasts = 2;
        });

        captured!.Position = QuasarToastPosition.TopCenter;
        captured.MaxVisibleToasts = 9;

        using var provider = services.BuildServiceProvider();
        var registered = provider.GetRequiredService<QuasarToastHostOptions>();

        Assert.Equal(QuasarToastPosition.BottomLeft, registered.Position);
        Assert.Equal(2, registered.MaxVisibleToasts);
    }

    [Fact]
    public void InvalidVisibleToastLimitNamesTheOption()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddQuasarUi(options => options.MaxVisibleToasts = 0));

        Assert.Contains("MaxVisibleToasts", error.Message, StringComparison.Ordinal);
    }
}
