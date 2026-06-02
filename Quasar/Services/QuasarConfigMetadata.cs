using System.Globalization;
using System.Reflection;
using Quasar.Models;

namespace Quasar.Services;

public enum QuasarConfigOptionScope
{
    Root,
    Session,
}

public enum QuasarConfigOptionKind
{
    Boolean,
    Integer,
    Decimal,
    Text,
    LongText,
    SelectInteger,
    SelectText,
}

public sealed record QuasarConfigOptionCategory(string Key, string Title, int Order, string Description);

public sealed record QuasarConfigSelectOption(int Value, string Label, string XmlName = "");

public sealed record QuasarConfigSelectTextOption(string Value, string Label);

public sealed class QuasarConfigOptionDefinition
{
    public required QuasarConfigOptionScope Scope { get; init; }

    public required string PropertyName { get; init; }

    public required string ElementName { get; init; }

    public required string CategoryKey { get; init; }

    public required string Label { get; init; }

    public required QuasarConfigOptionKind Kind { get; init; }

    public string HelperText { get; init; } = string.Empty;

    public int Order { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double? Step { get; init; }

    public IReadOnlyList<QuasarConfigSelectOption> SelectOptions { get; init; } = [];

    public IReadOnlyList<QuasarConfigSelectTextOption> SelectTextOptions { get; init; } = [];

    public string SearchAliases { get; init; } = string.Empty;

    private string SearchBlob =>
        string.Join(
            ' ',
            Label,
            HelperText,
            PropertyName,
            ElementName,
            SearchAliases);

    public bool Matches(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        var terms = searchText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return terms.All(term => SearchBlob.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}

public static class QuasarConfigMetadata
{
    public static readonly IReadOnlyList<QuasarConfigOptionCategory> Categories =
    [
        new("server", "Server", 10, "Reusable server-facing options."),
        new("automation", "Automation", 20, "Restart and watchdog behavior."),
        new("moderation", "Moderation", 30, "Chat and logging behavior."),
        new("general", "General", 40, "Core world settings and hard limits."),
        new("multipliers", "Multipliers", 50, "Economy and progression multipliers."),
        new("player", "Player & Progression", 60, "Player experience, respawn, and QoL."),
        new("combat", "Blocks & Combat", 70, "Combat rules and block behavior."),
        new("npcs", "NPCs & World", 80, "NPC spawning, environment, and planet rules."),
        new("trash", "Trash & Cleanup", 90, "Cleanup thresholds and sync limits."),
        new("economy", "Economy & Contracts", 100, "Stations, economy, and contract tuning."),
        new("advanced", "Advanced", 110, "Low-level and scenario-oriented settings."),
    ];

    public static readonly IReadOnlyList<QuasarConfigOptionDefinition> Options =
    [
        RootText("ServerDescription", "ServerDescription", "server", "Server Description", 10, QuasarConfigOptionKind.LongText, "Shown to clients in server browsers."),
        RootText("MessageOfTheDay", "MessageOfTheDay", "server", "Message of the Day", 20, QuasarConfigOptionKind.LongText),
        RootText("MessageOfTheDayUrl", "MessageOfTheDayUrl", "server", "MOTD URL", 30, searchAliases: "motd message url"),
        RootBool("CrossPlatform", "CrossPlatform", "server", "Cross Platform", 40),
        RootBool("VerboseNetworkLogging", "VerboseNetworkLogging", "server", "Verbose Network Logging", 60),
        RootBool("PauseGameWhenEmpty", "PauseGameWhenEmpty", "server", "Pause Game When Empty", 70),

        RootBool("AutoRestartEnabled", "AutoRestartEnabled", "automation", "Enable Auto-Restart", 10),
        RootInt("AutoRestartTimeInMin", "AutoRestatTimeInMin", "automation", "Auto-Restart Interval (min)", 20, min: 0, helperText: "Vanilla field name keeps original typo."),
        RootBool("AutoRestartSave", "AutoRestartSave", "automation", "Save Before Restart", 30),
        RootBool("AutoUpdateEnabled", "AutoUpdateEnabled", "automation", "Enable Auto-Update", 40),
        RootInt("AutoUpdateCheckIntervalInMin", "AutoUpdateCheckIntervalInMin", "automation", "Update Check Interval (min)", 50, min: 1),
        RootInt("AutoUpdateRestartDelayInMin", "AutoUpdateRestartDelayInMin", "automation", "Update Restart Delay (min)", 60, min: 0),
        RootText("AutoUpdateSteamBranch", "AutoUpdateSteamBranch", "automation", "Steam Branch", 70),
        RootDecimal("WatcherInterval", "WatcherInterval", "automation", "Watcher Interval (sec)", 80, min: 1, step: 1),
        RootDecimal("WatcherSimulationSpeedMinimum", "WatcherSimulationSpeedMinimum", "automation", "Min Simulation Speed", 90, min: 0, max: 1, step: 0.01),
        RootInt("ManualActionDelay", "ManualActionDelay", "automation", "Manual Action Delay (min)", 100, min: 0),
        RootText("ManualActionChatMessage", "ManualActionChatMessage", "automation", "Manual Action Chat Message", 110, helperText: "Use {0} for minute countdown."),
        RootBool("AutodetectDependencies", "AutodetectDependencies", "automation", "Autodetect Dependencies", 120),

        RootBool("SaveChatToLog", "SaveChatToLog", "moderation", "Save Chat To Log", 10),
        RootSelectText("NetworkType", "NetworkType", "moderation", "Network Type", 20, [new(nameof(QuasarNetworkType.Steam), "Steam"), new(nameof(QuasarNetworkType.EOS), "EOS")], helperText: "Controls Steam or EOS networking and mod source resolution.", searchAliases: "steam eos epic mods workshop"),
        RootBool("ConsoleCompatibility", "ConsoleCompatibility", "moderation", "Console Compatibility", 30),
        RootBool("ChatAntiSpamEnabled", "ChatAntiSpamEnabled", "moderation", "Enable Chat Anti-Spam", 40),
        RootInt("SameMessageTimeout", "SameMessageTimeout", "moderation", "Same Message Timeout (sec)", 50, min: 0),
        RootDecimal("SpamMessagesTime", "SpamMessagesTime", "moderation", "Spam Detection Window (sec)", 60, min: 0, step: 0.1),
        RootInt("SpamMessagesTimeout", "SpamMessagesTimeout", "moderation", "Spam Timeout (sec)", 70, min: 0),

        SessionSelect("GameMode", "GameMode", "general", "Game Mode", 10, [new(0, "Creative", "Creative"), new(1, "Survival", "Survival")]),
        SessionSelect("OnlineMode", "OnlineMode", "general", "Online Mode", 20, [new(0, "Offline", "OFFLINE"), new(1, "Public", "PUBLIC"), new(2, "Friends", "FRIENDS"), new(3, "Private", "PRIVATE")]),
        SessionInt("MaxPlayers", "MaxPlayers", "server", "Max Players", 35, min: 1),
        SessionInt("MaxFloatingObjects", "MaxFloatingObjects", "general", "Max Floating Objects", 40, min: 0),
        SessionInt("TotalBotLimit", "TotalBotLimit", "general", "Total Bot Limit", 50, min: 0),
        SessionInt("MaxBackupSaves", "MaxBackupSaves", "general", "Max Backup Saves", 60, min: 0),
        SessionInt("MaxGridSize", "MaxGridSize", "general", "Max Grid Size", 70, min: 0),
        SessionInt("MaxBlocksPerPlayer", "MaxBlocksPerPlayer", "general", "Max Blocks Per Player", 80, min: 0),
        SessionInt("TotalPcu", "TotalPCU", "general", "Total PCU", 90, min: 0),
        SessionInt("PiratePcu", "PiratePCU", "general", "Pirate PCU", 100, min: 0),
        SessionInt("GlobalEncounterPcu", "GlobalEncounterPCU", "general", "Global Encounter PCU", 110, min: 0),
        SessionInt("MaxFactionsCount", "MaxFactionsCount", "general", "Max Factions Count", 120, min: 0),
        SessionInt("WorldSizeKm", "WorldSizeKm", "general", "World Size (km)", 130, min: 0, helperText: "0 means unlimited."),
        SessionInt("ViewDistance", "ViewDistance", "general", "View Distance", 140, min: 1000),

        SessionDecimal("InventorySizeMultiplier", "InventorySizeMultiplier", "multipliers", "Inventory Size Multiplier", 10, min: 0, step: 0.1),
        SessionDecimal("BlocksInventorySizeMultiplier", "BlocksInventorySizeMultiplier", "multipliers", "Blocks Inventory Multiplier", 20, min: 0, step: 0.1),
        SessionDecimal("AssemblerSpeedMultiplier", "AssemblerSpeedMultiplier", "multipliers", "Assembler Speed Multiplier", 30, min: 0, step: 0.1),
        SessionDecimal("AssemblerEfficiencyMultiplier", "AssemblerEfficiencyMultiplier", "multipliers", "Assembler Efficiency Multiplier", 40, min: 0, step: 0.1),
        SessionDecimal("RefinerySpeedMultiplier", "RefinerySpeedMultiplier", "multipliers", "Refinery Speed Multiplier", 50, min: 0, step: 0.1),
        SessionDecimal("WelderSpeedMultiplier", "WelderSpeedMultiplier", "multipliers", "Welder Speed Multiplier", 60, min: 0, step: 0.1),
        SessionDecimal("GrinderSpeedMultiplier", "GrinderSpeedMultiplier", "multipliers", "Grinder Speed Multiplier", 70, min: 0, step: 0.1),
        SessionDecimal("HackSpeedMultiplier", "HackSpeedMultiplier", "multipliers", "Hack Speed Multiplier", 80, min: 0, step: 0.01),
        SessionDecimal("SpawnShipTimeMultiplier", "SpawnShipTimeMultiplier", "multipliers", "Spawn Ship Time Multiplier", 90, min: 0, step: 0.1),
        SessionDecimal("ProceduralDensity", "ProceduralDensity", "multipliers", "Procedural Density", 100, min: 0, step: 0.1),
        SessionInt("ProceduralSeed", "ProceduralSeed", "multipliers", "Procedural Seed", 110),
        SessionDecimal("FloraDensityMultiplier", "FloraDensityMultiplier", "multipliers", "Flora Density Multiplier", 120, min: 0, step: 0.1),
        SessionDecimal("HarvestRatioMultiplier", "HarvestRatioMultiplier", "multipliers", "Harvest Ratio Multiplier", 130, min: 0, step: 0.1),

        SessionSelect("EnvironmentHostility", "EnvironmentHostility", "player", "Environment Hostility", 10, [new(0, "Safe", "SAFE"), new(1, "Normal", "NORMAL"), new(2, "Cataclysm", "CATACLYSM"), new(3, "Cataclysm Unreal", "CATACLYSM_UNREAL")]),
        SessionBool("AutoHealing", "AutoHealing", "player", "Auto Healing", 20),
        SessionBool("ShowPlayerNamesOnHud", "ShowPlayerNamesOnHud", "player", "Show Player Names On HUD", 30),
        SessionBool("EnableSpectator", "EnableSpectator", "player", "Enable Spectator", 40),
        SessionBool("RespawnShipDelete", "RespawnShipDelete", "player", "Delete Respawn Ship", 50),
        SessionBool("PermanentDeath", "PermanentDeath", "player", "Permanent Death", 60),
        SessionBool("EnableSaving", "EnableSaving", "player", "Enable Saving", 70),
        SessionBool("EnableContainerDrops", "EnableContainerDrops", "player", "Enable Container Drops", 80),
        SessionBool("Enable3rdPersonView", "Enable3rdPersonView", "player", "Enable 3rd Person View", 90, searchAliases: "third person"),
        SessionBool("EnableToolShake", "EnableToolShake", "player", "Enable Tool Shake", 100),
        SessionBool("EnableJetpack", "EnableJetpack", "player", "Enable Jetpack", 110),
        SessionBool("SpawnWithTools", "SpawnWithTools", "player", "Spawn With Tools", 120),
        SessionBool("EnableScripterRole", "EnableScripterRole", "player", "Enable Scripter Role", 130),
        SessionBool("EnableResearch", "EnableResearch", "player", "Enable Research", 140),
        SessionBool("EnableGoodBotHints", "EnableGoodBotHints", "player", "Enable Good Bot Hints", 150),
        SessionBool("EnableAutorespawn", "EnableAutorespawn", "player", "Enable Auto-Respawn", 160),

        SessionBool("EnableRemoteBlockRemoval", "EnableRemoteBlockRemoval", "combat", "Enable Remote Block Removal", 10),
        SessionBool("EnableCopyPaste", "EnableCopyPaste", "combat", "Enable Copy Paste", 20),
        SessionBool("WeaponsEnabled", "WeaponsEnabled", "combat", "Weapons Enabled", 30),
        SessionBool("ThrusterDamage", "ThrusterDamage", "combat", "Thruster Damage", 40),
        SessionBool("DestructibleBlocks", "DestructibleBlocks", "combat", "Destructible Blocks", 50),
        SessionBool("EnableVoxelDestruction", "EnableVoxelDestruction", "combat", "Enable Voxel Destruction", 60),
        SessionBool("InfiniteAmmo", "InfiniteAmmo", "combat", "Infinite Ammo", 70),
        SessionBool("EnableVoxelHand", "EnableVoxelHand", "combat", "Enable Voxel Hand", 80),
        SessionBool("EnableTurretsFriendlyFire", "EnableTurretsFriendlyFire", "combat", "Enable Turrets Friendly Fire", 90),
        SessionBool("EnableSubgridDamage", "EnableSubgridDamage", "combat", "Enable Subgrid Damage", 100),
        SessionBool("EnableConvertToStation", "EnableConvertToStation", "combat", "Enable Convert To Station", 110),
        SessionBool("StationVoxelSupport", "StationVoxelSupport", "combat", "Station Voxel Support", 120),
        SessionBool("EnableSelectivePhysicsUpdates", "EnableSelectivePhysicsUpdates", "combat", "Enable Selective Physics Updates", 130),
        SessionBool("EnableSupergridding", "EnableSupergridding", "combat", "Enable Supergridding", 140),

        SessionBool("CargoShipsEnabled", "CargoShipsEnabled", "npcs", "Cargo Ships Enabled", 10),
        SessionBool("EnableEncounters", "EnableEncounters", "npcs", "Enable Encounters", 20),
        SessionBool("EnableDrones", "EnableDrones", "npcs", "Enable Drones", 30),
        SessionInt("MaxDrones", "MaxDrones", "npcs", "Max Drones", 40, min: 0),
        SessionBool("EnableWolfs", "EnableWolfs", "npcs", "Enable Wolves", 50),
        SessionBool("EnableSpiders", "EnableSpiders", "npcs", "Enable Spiders", 60),
        SessionBool("EnableSunRotation", "EnableSunRotation", "npcs", "Enable Sun Rotation", 70),
        SessionDecimal("SunRotationIntervalMinutes", "SunRotationIntervalMinutes", "npcs", "Sun Rotation Interval (min)", 80, min: 0, step: 1),
        SessionBool("EnableOxygen", "EnableOxygen", "npcs", "Enable Oxygen", 90),
        SessionBool("EnableOxygenPressurization", "EnableOxygenPressurization", "npcs", "Enable Oxygen Pressurization", 100),
        SessionBool("WeatherSystem", "WeatherSystem", "npcs", "Weather System", 110),
        SessionBool("WeatherLightingDamage", "WeatherLightingDamage", "npcs", "Weather Lightning Damage", 120, searchAliases: "lighting lightning"),
        RootInt("AsteroidAmount", "AsteroidAmount", "npcs", "Asteroid Amount", 125, min: 0),
        SessionBool("PredefinedAsteroids", "PredefinedAsteroids", "npcs", "Predefined Asteroids", 130),
        SessionInt("MaxPlanets", "MaxPlanets", "npcs", "Max Planets", 140, min: 0),

        SessionBool("TrashRemovalEnabled", "TrashRemovalEnabled", "trash", "Trash Removal Enabled", 10),
        SessionInt("StopGridsPeriodMin", "StopGridsPeriodMin", "trash", "Stop Grids Period (min)", 20, min: 0),
        SessionInt("TrashFlagsValue", "TrashFlagsValue", "trash", "Trash Flags Value", 30, min: 0),
        SessionInt("AfkTimeoutMin", "AFKTimeountMin", "trash", "AFK Timeout (min)", 40, min: 0, searchAliases: "afk"),
        SessionInt("BlockCountThreshold", "BlockCountThreshold", "trash", "Block Count Threshold", 50, min: 0),
        SessionDecimal("PlayerDistanceThreshold", "PlayerDistanceThreshold", "trash", "Player Distance Threshold", 60, min: 0, step: 1),
        SessionInt("OptimalGridCount", "OptimalGridCount", "trash", "Optimal Grid Count", 70, min: 0),
        SessionDecimal("PlayerInactivityThreshold", "PlayerInactivityThreshold", "trash", "Player Inactivity Threshold", 80, min: 0, step: 0.1),
        SessionInt("PlayerCharacterRemovalThreshold", "PlayerCharacterRemovalThreshold", "trash", "Player Character Removal Threshold", 90, min: 0),
        SessionBool("VoxelTrashRemovalEnabled", "VoxelTrashRemovalEnabled", "trash", "Voxel Trash Removal Enabled", 100),
        SessionDecimal("VoxelPlayerDistanceThreshold", "VoxelPlayerDistanceThreshold", "trash", "Voxel Player Distance Threshold", 110, min: 0, step: 1),
        SessionDecimal("VoxelGridDistanceThreshold", "VoxelGridDistanceThreshold", "trash", "Voxel Grid Distance Threshold", 120, min: 0, step: 1),
        SessionInt("VoxelAgeThreshold", "VoxelAgeThreshold", "trash", "Voxel Age Threshold (hours)", 130, min: 0),
        SessionInt("RemoveOldIdentitiesH", "RemoveOldIdentitiesH", "trash", "Remove Old Identities (hours)", 140, min: 0),
        SessionInt("SyncDistance", "SyncDistance", "trash", "Sync Distance", 150, min: 0),

        SessionBool("EnableEconomy", "EnableEconomy", "economy", "Enable Economy", 10),
        SessionDecimal("DepositsCountCoefficient", "DepositsCountCoefficient", "economy", "Deposits Count Coefficient", 20, min: 0, step: 0.1),
        SessionDecimal("DepositSizeDenominator", "DepositSizeDenominator", "economy", "Deposit Size Denominator", 30, min: 0, step: 0.1),
        SessionInt("TradeFactionsCount", "TradeFactionsCount", "economy", "Trade Factions Count", 40, min: 0),
        SessionDecimal("StationsDistanceInnerRadius", "StationsDistanceInnerRadius", "economy", "Stations Inner Radius", 50, min: 0, step: 1000),
        SessionDecimal("StationsDistanceOuterRadiusStart", "StationsDistanceOuterRadiusStart", "economy", "Stations Outer Radius Start", 60, min: 0, step: 1000),
        SessionDecimal("StationsDistanceOuterRadiusEnd", "StationsDistanceOuterRadiusEnd", "economy", "Stations Outer Radius End", 70, min: 0, step: 1000),
        SessionInt("EconomyTickInSeconds", "EconomyTickInSeconds", "economy", "Economy Tick (sec)", 80, min: 0),
        SessionInt("NpcGridClaimTimeLimit", "NPCGridClaimTimeLimit", "economy", "NPC Grid Claim Time Limit", 90, min: 0),
        SessionBool("EnableBountyContracts", "EnableBountyContracts", "economy", "Enable Bounty Contracts", 100),
        SessionBool("EnablePcuTrading", "EnablePcuTrading", "economy", "Enable PCU Trading", 110),
        SessionBool("FamilySharing", "FamilySharing", "economy", "Family Sharing", 120),
        SessionBool("UseConsolePcu", "UseConsolePCU", "economy", "Use Console PCU", 130),
        SessionBool("OffensiveWordsFiltering", "OffensiveWordsFiltering", "economy", "Offensive Words Filtering", 140),

        SessionBool("ResetOwnership", "ResetOwnership", "advanced", "Reset Ownership", 10),
        SessionBool("RealisticSound", "RealisticSound", "advanced", "Realistic Sound", 20),
        SessionInt("VoxelGeneratorVersion", "VoxelGeneratorVersion", "advanced", "Voxel Generator Version", 30, min: 0),
        SessionBool("ScenarioEditMode", "ScenarioEditMode", "advanced", "Scenario Edit Mode", 40),
        SessionBool("Scenario", "Scenario", "advanced", "Scenario", 50),
        SessionBool("CanJoinRunning", "CanJoinRunning", "advanced", "Can Join Running", 60),
        SessionInt("PhysicsIterations", "PhysicsIterations", "advanced", "Physics Iterations", 70, min: 1),
        SessionBool("ExperimentalMode", "ExperimentalMode", "advanced", "Experimental Mode", 80),
        SessionBool("AdaptiveSimulationQuality", "AdaptiveSimulationQuality", "advanced", "Adaptive Simulation Quality", 90),
        SessionInt("MinDropContainerRespawnTime", "MinDropContainerRespawnTime", "advanced", "Min Drop Container Respawn Time", 100, min: 0),
        SessionInt("MaxDropContainerRespawnTime", "MaxDropContainerRespawnTime", "advanced", "Max Drop Container Respawn Time", 110, min: 0),
        SessionDecimal("OptimalSpawnDistance", "OptimalSpawnDistance", "advanced", "Optimal Spawn Distance", 120, min: 0, step: 100),
        SessionBool("SimplifiedSimulation", "SimplifiedSimulation", "advanced", "Simplified Simulation", 130),
    ];

    private static readonly IReadOnlyDictionary<string, PropertyInfo> RootProperties = typeof(QuasarWorldRootSettings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .ToDictionary(property => property.Name, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, PropertyInfo> SessionProperties = typeof(QuasarSessionSettings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .ToDictionary(property => property.Name, StringComparer.Ordinal);

    public static PropertyInfo GetProperty(QuasarConfigOptionDefinition option)
    {
        var source = option.Scope == QuasarConfigOptionScope.Root
            ? RootProperties
            : SessionProperties;

        return source[option.PropertyName];
    }

    public static string FormatValue(QuasarConfigOptionDefinition option, object target)
    {
        var property = GetProperty(option);
        var value = property.GetValue(target);
        if (value is null)
            return string.Empty;

        return option.Kind switch
        {
            QuasarConfigOptionKind.Boolean => ((bool)value) ? "true" : "false",
            QuasarConfigOptionKind.Integer => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
            QuasarConfigOptionKind.SelectInteger => FormatSelectInteger(option, value),
            QuasarConfigOptionKind.Decimal => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
            QuasarConfigOptionKind.SelectText when value is QuasarNetworkType networkType => networkType.ToConfigValue(),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string FormatSelectInteger(QuasarConfigOptionDefinition option, object value)
    {
        var intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        var match = option.SelectOptions.FirstOrDefault(choice => choice.Value == intValue);
        if (match is not null && !string.IsNullOrEmpty(match.XmlName))
            return match.XmlName;

        return intValue.ToString(CultureInfo.InvariantCulture);
    }

    private static QuasarConfigOptionDefinition RootBool(string propertyName, string elementName, string categoryKey, string label, int order, string helperText = "", string searchAliases = "") =>
        new()
        {
            Scope = QuasarConfigOptionScope.Root,
            PropertyName = propertyName,
            ElementName = elementName,
            CategoryKey = categoryKey,
            Label = label,
            Kind = QuasarConfigOptionKind.Boolean,
            Order = order,
            HelperText = helperText,
            SearchAliases = searchAliases,
        };

    private static QuasarConfigOptionDefinition RootInt(string propertyName, string elementName, string categoryKey, string label, int order, double? min = null, double? max = null, double? step = 1, string helperText = "", string searchAliases = "") =>
        new()
        {
            Scope = QuasarConfigOptionScope.Root,
            PropertyName = propertyName,
            ElementName = elementName,
            CategoryKey = categoryKey,
            Label = label,
            Kind = QuasarConfigOptionKind.Integer,
            Order = order,
            Min = min,
            Max = max,
            Step = step,
            HelperText = helperText,
            SearchAliases = searchAliases,
        };

    private static QuasarConfigOptionDefinition RootDecimal(string propertyName, string elementName, string categoryKey, string label, int order, double? min = null, double? max = null, double? step = 0.1, string helperText = "", string searchAliases = "") =>
        new()
        {
            Scope = QuasarConfigOptionScope.Root,
            PropertyName = propertyName,
            ElementName = elementName,
            CategoryKey = categoryKey,
            Label = label,
            Kind = QuasarConfigOptionKind.Decimal,
            Order = order,
            Min = min,
            Max = max,
            Step = step,
            HelperText = helperText,
            SearchAliases = searchAliases,
        };

    private static QuasarConfigOptionDefinition RootText(string propertyName, string elementName, string categoryKey, string label, int order, QuasarConfigOptionKind kind = QuasarConfigOptionKind.Text, string helperText = "", string searchAliases = "") =>
        new()
        {
            Scope = QuasarConfigOptionScope.Root,
            PropertyName = propertyName,
            ElementName = elementName,
            CategoryKey = categoryKey,
            Label = label,
            Kind = kind,
            Order = order,
            HelperText = helperText,
            SearchAliases = searchAliases,
        };

    private static QuasarConfigOptionDefinition RootSelectText(string propertyName, string elementName, string categoryKey, string label, int order, IReadOnlyList<QuasarConfigSelectTextOption> selectOptions, string helperText = "", string searchAliases = "") =>
        new()
        {
            Scope = QuasarConfigOptionScope.Root,
            PropertyName = propertyName,
            ElementName = elementName,
            CategoryKey = categoryKey,
            Label = label,
            Kind = QuasarConfigOptionKind.SelectText,
            Order = order,
            HelperText = helperText,
            SelectTextOptions = selectOptions,
            SearchAliases = searchAliases,
        };

    private static QuasarConfigOptionDefinition SessionBool(string propertyName, string elementName, string categoryKey, string label, int order, string helperText = "", string searchAliases = "") =>
        new()
        {
            Scope = QuasarConfigOptionScope.Session,
            PropertyName = propertyName,
            ElementName = elementName,
            CategoryKey = categoryKey,
            Label = label,
            Kind = QuasarConfigOptionKind.Boolean,
            Order = order,
            HelperText = helperText,
            SearchAliases = searchAliases,
        };

    private static QuasarConfigOptionDefinition SessionInt(string propertyName, string elementName, string categoryKey, string label, int order, double? min = null, double? max = null, double? step = 1, string helperText = "", string searchAliases = "") =>
        new()
        {
            Scope = QuasarConfigOptionScope.Session,
            PropertyName = propertyName,
            ElementName = elementName,
            CategoryKey = categoryKey,
            Label = label,
            Kind = QuasarConfigOptionKind.Integer,
            Order = order,
            Min = min,
            Max = max,
            Step = step,
            HelperText = helperText,
            SearchAliases = searchAliases,
        };

    private static QuasarConfigOptionDefinition SessionDecimal(string propertyName, string elementName, string categoryKey, string label, int order, double? min = null, double? max = null, double? step = 0.1, string helperText = "", string searchAliases = "") =>
        new()
        {
            Scope = QuasarConfigOptionScope.Session,
            PropertyName = propertyName,
            ElementName = elementName,
            CategoryKey = categoryKey,
            Label = label,
            Kind = QuasarConfigOptionKind.Decimal,
            Order = order,
            Min = min,
            Max = max,
            Step = step,
            HelperText = helperText,
            SearchAliases = searchAliases,
        };

    private static QuasarConfigOptionDefinition SessionSelect(string propertyName, string elementName, string categoryKey, string label, int order, IReadOnlyList<QuasarConfigSelectOption> selectOptions, string helperText = "", string searchAliases = "") =>
        new()
        {
            Scope = QuasarConfigOptionScope.Session,
            PropertyName = propertyName,
            ElementName = elementName,
            CategoryKey = categoryKey,
            Label = label,
            Kind = QuasarConfigOptionKind.SelectInteger,
            Order = order,
            HelperText = helperText,
            SelectOptions = selectOptions,
            SearchAliases = searchAliases,
        };
}
