using System.Globalization;
using Magnetar.Protocol.Model;

namespace Quasar.Services;

/// <summary>Admin-facing labels, separate from game and extension transport identifiers.</summary>
public static class EntityPresentation
{
    public static string TypeLabel(string typeTag) => typeTag switch
    {
        "Grid" => "Grid",
        "Character" => "Character",
        "Float" => "Dropped item",
        "Voxel" => "Terrain",
        _ => "Other entity",
    };

    public static string Details(EntitySummary entity) => (entity.TypeTag, entity.SubType) switch
    {
        ("Grid", "LargeStatic") => "Large-grid station",
        ("Grid", "SmallStatic") => "Small-grid station",
        ("Grid", "LargeShip") => "Large-grid ship",
        ("Grid", "SmallShip") => "Small-grid ship",
        ("Character", "Player") => "Player",
        ("Character", "Bot") => "NPC",
        ("Character", "Corpse") => "Dead character",
        ("Voxel", "Planet") => "Planet",
        ("Voxel", "Asteroid") => "Asteroid",
        ("Other", "MyProxyAntenna") => "Antenna relay",
        _ => "—",
    };

    public static string Name(EntitySummary entity)
    {
        // The agent uses the CLR type as a fallback name for unnamed world roots.
        if (entity.TypeTag == "Other" && entity.DisplayName == entity.SubType)
        {
            var details = Details(entity);
            return details != "—" ? details : "Unnamed entity";
        }
        return entity.DisplayName;
    }

    public static decimal? ItemAmount(EntitySummary entity)
    {
        if (entity.TypeTag != "Float")
            return null;
        if (entity.ItemAmount is { } amount)
            return amount;

        // Older agents put a culture-formatted quantity in SubType (e.g. "x10").
        var legacy = entity.SubType;
        if (legacy?.StartsWith("x", StringComparison.Ordinal) == true
            && decimal.TryParse(legacy[1..].Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out amount))
            return amount;
        return null;
    }

    public static string Quantity(EntitySummary entity) =>
        ItemAmount(entity)?.ToString("0.######", CultureInfo.CurrentCulture) ?? "—";
}
