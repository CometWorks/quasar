using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Client.Components;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ProcessPriorityWarningTests
{
    private const string OutdatedHelperError =
        "target executable is not a supported Magnetar launcher\n";

    [Fact]
    public void OutdatedHelperGetsTheReinstallCommand()
    {
        var warning = DedicatedServerSupervisor.DescribeNiceFailure(
            true,
            10,
            DedicatedServerProcessPriority.Low,
            OutdatedHelperError
        );

        Assert.NotNull(warning);
        Assert.Contains("may be outdated", warning);
        Assert.Contains("install.sh --install-renice-helper --no-build --no-enable", warning);
    }

    [Fact]
    public void MissingHelperIsNamedOnlyForRaisedPriorities()
    {
        Assert.Equal(
            "Install /usr/local/bin/quasar-renice to use High process priority on Linux.",
            DedicatedServerSupervisor.DescribeNiceFailure(
                false,
                -10,
                DedicatedServerProcessPriority.High,
                ""
            )
        );
        Assert.Null(
            DedicatedServerSupervisor.DescribeNiceFailure(
                false,
                10,
                DedicatedServerProcessPriority.Low,
                ""
            )
        );
    }

    [Fact]
    public void OtherHelperFailuresHaveNoFixToSuggest() =>
        Assert.Null(
            DedicatedServerSupervisor.DescribeNiceFailure(
                true,
                10,
                DedicatedServerProcessPriority.Low,
                "Operation not permitted"
            )
        );

    [Fact]
    public async Task DetailPanelShowsTheWarningUntilItIsCleared()
    {
        await using var context = new QuasarBunitContext();
        var warning = DedicatedServerSupervisor.DescribeNiceFailure(
            true,
            10,
            DedicatedServerProcessPriority.Low,
            OutdatedHelperError
        )!;
        var row = FleetDashboardTestTransport.Row();
        var panel = context.Render<FleetRuntimeDetails>(p => p.Add(x => x.Row, row with { PriorityWarning = warning }));
        Assert.Equal(warning, panel.Find(".quasar-notice-warning").TextContent);
        panel.Render(p => p.Add(x => x.Row, row));
        Assert.Empty(panel.FindAll(".quasar-notice-warning"));
    }
}
