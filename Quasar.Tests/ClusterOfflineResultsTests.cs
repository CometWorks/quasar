using System.Text.Json;
using Bunit;
using Quasar.Components.Dashboard;
using CometWorks.ClusterGateway.AdminContract.V1;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterOfflineResultsTests
{
    [Theory]
    [InlineData("Exact")]
    [InlineData("Approximate")]
    [InlineData("Frozen")]
    [InlineData("Creative")]
    public async Task ResultsKeepConfidenceAndSharedCostsVisible(string confidence)
    {
        // Exact field names and units from cluster OfflineProfile.cs / OfflineModel.cs.
        var result = JsonSerializer.Deserialize<OfflineCompensationResult>("""
            {"Group":17,"FromFrame":225000,"CompensatedTo":1125000,"Frames":225000,
             "Capped":true,"Confidence":"Approximate","EnergyDelta":-0.25,
             "ItemDelta":{"MyObjectBuilder_Ingot/Uranium":-0.08,"MyObjectBuilder_Component/SteelPlate":109},
             "ProducerEnergyMWh":{"31":2.5},"ConsumerEnergyMWh":{"32":2.25},
             "StorageInputEnergyMWh":{"33":0.5},
             "ProducerFuelConsumed":{"31":{"MyObjectBuilder_Ingot/Uranium":0.08}},
             "ProductionUnits":{"34":109},"UpkeepConsumed":{"32":1},
             "Zones":[{"Block":32,"Exists":false,"Activating":false,"Exhausted":true,"RemainingFrames":0,"ActivationRemainingFrames":0},
                      {"Block":35,"Exists":true,"Activating":true,"Exhausted":false,"RemainingFrames":3600,"ActivationRemainingFrames":7200}]}
            """)! with { Confidence = confidence };
        await using var context = new BunitContext();
        var panel = context.Render<ClusterOfflineResults>(p => p.Add(c => c.Results, [result]).Add(c => c.Prediction, true));
        Assert.Contains("Prediction only", panel.Markup);
        Assert.Contains(confidence, panel.Markup);
        Assert.Contains("1 game hours", panel.Markup);
        Assert.Contains("Capped; excess time", panel.Markup);
        Assert.Contains("Stored energy change: -0.25 MWh", panel.Markup);
        Assert.Contains("MyObjectBuilder_Ingot/Uranium", panel.Markup);
        Assert.Contains("0.08", panel.Markup);
        Assert.Contains("109", panel.Markup);
        Assert.Contains("chips exhausted", panel.Markup);
        Assert.Contains("activation remaining 120 seconds", panel.Markup);
        Assert.Contains("recipe units", panel.Markup);
        Assert.DoesNotContain("<button", panel.Markup);
        if (Environment.GetEnvironmentVariable("QUASAR_OFFLINE_RENDER_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"results-{confidence}.html"),
                "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Offline compensation fixture</title><body>" + panel.Markup + "</body></html>");
        }
    }

    [Fact]
    public async Task MissingLegacyAndSkippedResultsDoNotInventZeroCosts()
    {
        await using var context = new BunitContext();
        var panel = context.Render<ClusterOfflineResults>();
        Assert.Contains("results unavailable", panel.Markup);
        panel.Render(p => p.Add(c => c.Results, []));
        Assert.Contains("legacy Final", panel.Markup);
        panel.Render(p => p.Add(c => c.Results, [Skipped()]));
        Assert.Contains("Skipped: content mismatch", panel.Markup);
        Assert.DoesNotContain("Stored energy change", panel.Markup);
    }

    private static OfflineCompensationResult Skipped() => new(1, 0, 0, 0, 0, false, "Frozen", "content mismatch", 0,
        new Dictionary<string, decimal>(), new Dictionary<long, double>(), new Dictionary<long, double>(),
        new Dictionary<long, double>(), new Dictionary<long, Dictionary<string, decimal>>(),
        new Dictionary<long, double>(), new Dictionary<long, decimal>(), [], []);
}
