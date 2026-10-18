using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Components.Dashboard;
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

    // The Cards view and the expanded List row both show the warning through
    // ServerDetailPanel, next to status messages that come and go.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetailPanelShowsTheWarningUntilItIsCleared(bool collapseDetails)
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.AddAuthorization().SetAuthorized("viewer");
        using var profiles = new QuasarConfigProfileCatalog(
            NullLogger<QuasarConfigProfileCatalog>.Instance
        );
        context.Services.AddSingleton(new AgentRegistry(null!, null!, null!, null!));
        context.Services.AddSingleton(profiles);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        var warning = DedicatedServerSupervisor.DescribeNiceFailure(
            true,
            10,
            DedicatedServerProcessPriority.Low,
            OutdatedHelperError
        )!;
        var runtime = new DedicatedServerRuntimeSnapshot
        {
            UniqueName = "alpha",
            State = DedicatedServerProcessState.Running,
            LastMessage = "Server online.",
            ProcessPriorityWarning = warning,
        };

        var panel = context.Render<ServerDetailPanel>(p =>
            p.Add(x => x.Runtime, runtime).Add(x => x.CollapseDetails, collapseDetails)
        );

        Assert.Equal(warning, panel.Find(".quasar-notice-warning").TextContent);

        runtime.ProcessPriorityWarning = string.Empty;
        panel.Render();

        Assert.Empty(panel.FindAll(".quasar-notice-warning"));
    }
}
