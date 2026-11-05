using System.Text.Json;
using Magnetar.Protocol.Model;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class EntityPresentationTests
{
    [Theory]
    [InlineData("x10", "10")]
    [InlineData("x0.25", "0.25")]
    [InlineData("x0,25", "0.25")]
    [InlineData("Item", null)]
    [InlineData("xinvalid", null)]
    public void LegacyStacksHaveQuantityInsteadOfSubtypeDetails(string subtype, string? expected)
    {
        var entity = JsonSerializer.Deserialize<EntitySummary>(
            $$"""{"TypeTag":"Float","SubType":"{{subtype}}","DisplayName":"SteelPlate"}""")!;
        Assert.Equal("Dropped item", EntityPresentation.TypeLabel(entity.TypeTag));
        Assert.Equal("—", EntityPresentation.Details(entity));
        Assert.Equal(expected is null ? (decimal?)null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            EntityPresentation.ItemAmount(entity));
        Assert.Equal(subtype, entity.SubType); // Keep raw identifiers intact for extensions.
    }

    [Fact]
    public void NumericStackAmountRoundTripsWithoutLosingPrecision()
    {
        var entity = new EntitySummary { TypeTag = "Float", SubType = "SteelPlate", DisplayName = "Steel plate", ItemAmount = 123456.123456m };
        entity = JsonSerializer.Deserialize<EntitySummary>(JsonSerializer.Serialize(entity))!;
        Assert.Equal(123456.123456m, EntityPresentation.ItemAmount(entity));
        Assert.Equal("Steel plate", EntityPresentation.Name(entity));
        entity.TypeTag = "Grid";
        Assert.Equal("—", EntityPresentation.Quantity(entity));
    }

    [Theory]
    [InlineData("Grid", "LargeStatic", "Grid", "Large-grid station")]
    [InlineData("Grid", "SmallShip", "Grid", "Small-grid ship")]
    [InlineData("Character", "Bot", "Character", "NPC")]
    [InlineData("Voxel", "Asteroid", "Terrain", "Asteroid")]
    [InlineData("Other", "MyProxyAntenna", "Other entity", "Antenna relay")]
    [InlineData("Other", "MyUnknownEntity", "Other entity", "—")]
    [InlineData("FutureType", "InternalDetail", "Other entity", "—")]
    public void AdminLabelsDoNotExposeImplementationNames(string type, string subtype, string label, string details)
    {
        var entity = new EntitySummary { TypeTag = type, SubType = subtype };
        Assert.Equal(label, EntityPresentation.TypeLabel(type));
        Assert.Equal(details, EntityPresentation.Details(entity));
        Assert.Equal("—", EntityPresentation.Quantity(entity));
    }

    [Theory]
    [InlineData("MyProxyAntenna", "MyProxyAntenna", "Antenna relay")]
    [InlineData("MyUnknownEntity", "MyUnknownEntity", "Unnamed entity")]
    [InlineData("Custom antenna name", "MyProxyAntenna", "Custom antenna name")]
    public void UnnamedOtherEntitiesDoNotLeakClrTypes(string name, string subtype, string expected)
    {
        Assert.Equal(expected, EntityPresentation.Name(new() { TypeTag = "Other", SubType = subtype, DisplayName = name }));
    }
}
