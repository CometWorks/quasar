namespace Quasar.Services.Discord;

public sealed class DeathMessagesConfig
{
    public List<string> SuicideMessages { get; set; } =
    [
        "{victim} restarted their character",
        "{victim} chose easy way out",
        "{victim} decided to leave this world",
        "{victim} embraced void",
        "{victim} took matters into their own hands",
        "{victim} pressed big red button... on themselves",
    ];

    public List<string> PvPMessages { get; set; } =
    [
        "{killer} killed {victim} with {weapon}",
        "{killer} eliminated {victim} with {weapon}",
        "{killer} sent {victim} to respawn with {weapon}",
        "{victim} met their end at hands of {killer} wielding {weapon}",
        "{killer} showed {victim} no mercy with {weapon}",
        "{victim} was destroyed by {killer} using {weapon}",
    ];

    public List<string> TurretMessages { get; set; } =
    [
        "{killer} killed {victim} with {weapon}",
        "{victim} walked into {killer}'s {weapon}",
        "{killer} sent {victim} to void with {weapon}",
        "{killer} ruined {victim}'s day with {weapon}",
        "{killer}'s {weapon} caught {victim} off guard",
        "{victim} met {killer}'s {weapon}",
    ];

    public List<string> GridMessages { get; set; } =
    [
        "{victim} was run over by ship",
        "{victim} got too close to moving grid",
        "{victim} was crushed",
        "{victim} met business end of landing gear",
        "Lord Clang claimed {victim}",
        "{victim} was flattened by ship",
    ];

    public List<string> OxygenMessages { get; set; } =
    [
        "{victim} stopped breathing due to lack of oxygen",
        "{victim} forgot to check oxygen levels",
        "{victim} ran out of air",
        "{victim} discovered that space has no oxygen",
        "{victim} suffocated",
    ];

    public List<string> PressureMessages { get; set; } =
    [
        "{victim} died from environmental pressure",
        "{victim} couldn't handle pressure",
        "{victim} experienced rapid decompression",
        "{victim}'s suit failed under pressure",
        "{victim} popped like balloon",
    ];

    public List<string> CollisionMessages { get; set; } =
    [
        "{victim} hit something very fast",
        "{victim} fell from great height",
        "{victim} died in collision",
        "{victim} forgot gravity exists",
        "{victim} learned ground is hard",
        "{victim} experienced rapid unplanned landing",
    ];

    public List<string> AccidentMessages { get; set; } =
    [
        "{victim} died in accident",
        "{victim} is no more",
        "{victim} met unfortunate end",
        "{victim} experienced rapid unplanned disassembly",
        "{victim} disconnected from life unexpectedly",
        "Error 404: {victim}'s pulse not found",
    ];

    public string GetRandomMessage(string deathType)
    {
        var messages = GetMessagesForType(deathType);
        if (messages.Count == 0)
            return "{victim} died";

        return messages[Random.Shared.Next(messages.Count)];
    }

    public static DeathMessagesConfig CreateDefault()
    {
        return new DeathMessagesConfig();
    }

    public DeathMessagesConfig Clone()
    {
        return new DeathMessagesConfig
        {
            SuicideMessages = [.. SuicideMessages],
            PvPMessages = [.. PvPMessages],
            TurretMessages = [.. TurretMessages],
            GridMessages = [.. GridMessages],
            OxygenMessages = [.. OxygenMessages],
            PressureMessages = [.. PressureMessages],
            CollisionMessages = [.. CollisionMessages],
            AccidentMessages = [.. AccidentMessages],
        };
    }

    private List<string> GetMessagesForType(string deathType)
    {
        return deathType switch
        {
            "Suicide" => SuicideMessages,
            "PvP" => PvPMessages,
            "Turret" => TurretMessages,
            "Grid" => GridMessages,
            "Oxygen" => OxygenMessages,
            "Pressure" => PressureMessages,
            "Collision" => CollisionMessages,
            "Accident" => AccidentMessages,
            _ => AccidentMessages,
        };
    }
}
