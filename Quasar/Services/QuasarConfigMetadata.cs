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
    Password,
    KeyValueText,
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
        new("survival", "Survival", 50, "Game mode, production, respawn, oxygen/radiation, hunger, and survival progression."),
        new("multipliers", "Multipliers", 60, "World generation and remaining world multipliers."),
        new("player", "Player & Progression", 70, "Player experience, camera, and QoL."),
        new("combat", "Blocks & Combat", 80, "Combat rules and block behavior."),
        new("npcs", "NPCs & World", 90, "NPC spawning, environment, and planet rules."),
        new("trash", "Trash & Cleanup", 100, "Cleanup thresholds and sync limits."),
        new("economy", "Economy & Contracts", 110, "Stations, economy, and contract tuning."),
        new("advanced", "Advanced", 120, "Low-level and scenario-oriented settings."),
    ];

    public static readonly IReadOnlyList<QuasarConfigOptionDefinition> Options =
    [
        RootText("ServerDescription", "ServerDescription", "server", "Server Description", 10, QuasarConfigOptionKind.LongText, "Shown to clients in server browsers."),
        RootText("ServerPassword", "", "server", "Server Password", 20, QuasarConfigOptionKind.Password, "Written to the DS config as ServerPasswordHash/ServerPasswordSalt, not plaintext."),
        RootText("MessageOfTheDay", "MessageOfTheDay", "server", "Message of the Day", 30, QuasarConfigOptionKind.LongText),
        RootText("MessageOfTheDayUrl", "MessageOfTheDayUrl", "server", "MOTD URL", 40, searchAliases: "motd message url"),
        RootBool("CrossPlatform", "CrossPlatform", "server", "Cross Platform", 50),
        RootBool("VerboseNetworkLogging", "VerboseNetworkLogging", "server", "Verbose Network Logging", 70),
        RootBool("PauseGameWhenEmpty", "PauseGameWhenEmpty", "server", "Pause Game When Empty", 80),

        RootBool("AutoRestartEnabled", "AutoRestartEnabled", "automation", "Enable Auto-Restart", 10),
        RootInt("AutoRestartTimeInMin", "AutoRestatTimeInMin", "automation", "Auto-Restart Interval (min)", 20, min: 0, helperText: "Vanilla field name keeps original typo."),
        RootBool("AutoRestartSave", "AutoRestartSave", "automation", "Save Before Restart", 30),
        RootBool("AutoUpdateEnabled", "AutoUpdateEnabled", "automation", "Enable Auto-Update", 40),
        RootInt("AutoUpdateCheckIntervalInMin", "AutoUpdateCheckIntervalInMin", "automation", "Update Check Interval (min)", 50, min: 1),
        RootInt("AutoUpdateRestartDelayInMin", "AutoUpdateRestartDelayInMin", "automation", "Update Restart Delay (min)", 60, min: 0),
        RootText("AutoUpdateSteamBranch", "AutoUpdateSteamBranch", "automation", "Steam Branch", 70),
        RootDecimal("WatcherInterval", "WatcherInterval", "automation", "Watcher Interval (sec)", 80, min: 1, step: 1),
        RootDecimal("WatcherSimulationSpeedMinimum", "WatcherSimulationSpeedMinimum", "automation", "Min Simulation Speed", 90, min: 0, max: 1.1, step: 0.01),
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

        SessionSelect("GameMode", "GameMode", "survival", "Game Mode", 10, [new(0, "Creative", "Creative"), new(1, "Survival", "Survival")]),
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
        SessionInt("MinimumWorldSize", "MinimumWorldSize", "general", "Minimum World Size (km)", 150, min: 0),
        SessionInt("MaxHudChatMessageCount", "MaxHudChatMessageCount", "general", "Max HUD Chat Messages", 160, min: 0),

        SessionDecimal("InventorySizeMultiplier", "InventorySizeMultiplier", "survival", "Inventory Size Multiplier", 20, min: 0, step: 0.1),
        SessionDecimal("BlocksInventorySizeMultiplier", "BlocksInventorySizeMultiplier", "survival", "Blocks Inventory Multiplier", 30, min: 0, step: 0.1),
        SessionDecimal("AssemblerSpeedMultiplier", "AssemblerSpeedMultiplier", "survival", "Assembler Speed Multiplier", 40, min: 0, step: 0.1),
        SessionDecimal("AssemblerEfficiencyMultiplier", "AssemblerEfficiencyMultiplier", "survival", "Assembler Efficiency Multiplier", 50, min: 0, step: 0.1),
        SessionDecimal("RefinerySpeedMultiplier", "RefinerySpeedMultiplier", "survival", "Refinery Speed Multiplier", 60, min: 0, step: 0.1),
        SessionDecimal("WelderSpeedMultiplier", "WelderSpeedMultiplier", "survival", "Welder Speed Multiplier", 70, min: 0, step: 0.1),
        SessionDecimal("GrinderSpeedMultiplier", "GrinderSpeedMultiplier", "survival", "Grinder Speed Multiplier", 80, min: 0, step: 0.1),
        SessionDecimal("HackSpeedMultiplier", "HackSpeedMultiplier", "survival", "Hack Speed Multiplier", 90, min: 0, step: 0.01),
        SessionDecimal("SpawnShipTimeMultiplier", "SpawnShipTimeMultiplier", "survival", "Spawn Ship Time Multiplier", 100, min: 0, step: 0.1),
        SessionDecimal("ProceduralDensity", "ProceduralDensity", "multipliers", "Procedural Density", 100, min: 0, step: 0.1),
        SessionInt("ProceduralSeed", "ProceduralSeed", "multipliers", "Procedural Seed", 110),
        SessionDecimal("FloraDensityMultiplier", "FloraDensityMultiplier", "multipliers", "Flora Density Multiplier", 120, min: 0, step: 0.1),
        SessionDecimal("HarvestRatioMultiplier", "HarvestRatioMultiplier", "survival", "Harvest Ratio Multiplier", 110, min: 0, step: 0.1),

        SessionSelect("EnvironmentHostility", "EnvironmentHostility", "survival", "Environment Hostility", 120, [new(0, "Safe", "SAFE"), new(1, "Normal", "NORMAL"), new(2, "Cataclysm", "CATACLYSM"), new(3, "Cataclysm Unreal", "CATACLYSM_UNREAL")]),
        SessionBool("AutoHealing", "AutoHealing", "survival", "Auto Healing", 130),
        SessionDecimal("EnvironmentDamageMultiplier", "EnvironmentDamageMultiplier", "survival", "Environment Damage Multiplier", 135, min: 0, max: 2, step: 0.1),
        SessionBool("ShowPlayerNamesOnHud", "ShowPlayerNamesOnHud", "player", "Show Player Names On HUD", 30),
        SessionBool("EnableSpectator", "EnableSpectator", "player", "Enable Spectator", 40),
        SessionBool("RespawnShipDelete", "RespawnShipDelete", "survival", "Delete Respawn Ship", 150),
        SessionBool("EnableRespawnShips", "EnableRespawnShips", "survival", "Enable Respawn Ships", 160),
        SessionBool("PermanentDeath", "PermanentDeath", "survival", "Permanent Death", 140),
        SessionBool("EnableSaving", "EnableSaving", "player", "Enable Saving", 70),
        SessionInt("AutoSaveInMinutes", "AutoSaveInMinutes", "player", "Autosave Interval (min)", 80, min: 0),
        SessionBool("EnableContainerDrops", "EnableContainerDrops", "survival", "Enable Container Drops", 200),
        SessionBool("Enable3rdPersonView", "Enable3rdPersonView", "player", "Enable 3rd Person View", 90, searchAliases: "third person"),
        SessionBool("EnableToolShake", "EnableToolShake", "player", "Enable Tool Shake", 100),
        SessionBool("BlueprintShare", "BlueprintShare", "player", "Enable Blueprint Share", 110),
        SessionInt("BlueprintShareTimeout", "BlueprintShareTimeout", "player", "Blueprint Share Timeout", 120, min: 0),
        SessionBool("EnableSpaceSuitRespawn", "EnableSpaceSuitRespawn", "survival", "Enable Space Suit Respawn", 175),
        SessionDecimal("BackpackDespawnTimer", "BackpackDespawnTimer", "survival", "Backpack Despawn Time (min)", 176, min: 0, max: 10, step: 0.5),
        SessionBool("EnableJetpack", "EnableJetpack", "survival", "Enable Jetpack", 180),
        SessionBool("SpawnWithTools", "SpawnWithTools", "survival", "Spawn With Tools", 190),
        SessionBool("EnableScripterRole", "EnableScripterRole", "player", "Enable Scripter Role", 130),
        SessionDecimal("CharacterSpeedMultiplier", "CharacterSpeedMultiplier", "player", "Character Speed Multiplier", 135, min: 0.75, max: 1, step: 0.01),
        SessionBool("EnableRecoil", "EnableRecoil", "player", "Enable Weapon Recoil", 136),
        SessionBool("EnableGamepadAimAssist", "EnableGamepadAimAssist", "player", "Enable Gamepad Aim Assist", 137),
        SessionBool("EnableResearch", "EnableResearch", "survival", "Enable Research", 270),
        SessionBool("EnableGoodBotHints", "EnableGoodBotHints", "survival", "Enable Good Bot Hints", 280),
        SessionBool("EnableAutorespawn", "EnableAutorespawn", "survival", "Enable Auto-Respawn", 170),

        SessionBool("EnableRemoteBlockRemoval", "EnableRemoteBlockRemoval", "combat", "Enable Remote Block Removal", 10),
        SessionBool("EnableCopyPaste", "EnableCopyPaste", "combat", "Enable Copy Paste", 20),
        SessionBool("WeaponsEnabled", "WeaponsEnabled", "combat", "Weapons Enabled", 30),
        SessionBool("ThrusterDamage", "ThrusterDamage", "combat", "Thruster Damage", 40),
        SessionBool("DestructibleBlocks", "DestructibleBlocks", "combat", "Destructible Blocks", 50),
        SessionBool("EnableVoxelDestruction", "EnableVoxelDestruction", "combat", "Enable Voxel Destruction", 60),
        SessionBool("InfiniteAmmo", "InfiniteAmmo", "survival", "Infinite Ammo", 260),
        SessionBool("EnableVoxelHand", "EnableVoxelHand", "combat", "Enable Voxel Hand", 80),
        SessionBool("EnableTurretsFriendlyFire", "EnableTurretsFriendlyFire", "combat", "Enable Turrets Friendly Fire", 90),
        SessionBool("EnableSubgridDamage", "EnableSubgridDamage", "combat", "Enable Subgrid Damage", 100),
        SessionBool("EnableConvertToStation", "EnableConvertToStation", "combat", "Enable Convert To Station", 110),
        SessionBool("StationVoxelSupport", "StationVoxelSupport", "combat", "Station Voxel Support", 120),
        SessionBool("EnableSelectivePhysicsUpdates", "EnableSelectivePhysicsUpdates", "combat", "Enable Selective Physics Updates", 130),
        SessionBool("EnableSupergridding", "EnableSupergridding", "combat", "Enable Supergridding", 140),
        SessionSelect("BlockLimitsEnabled", "BlockLimitsEnabled", "combat", "Block Limits Mode", 150, [new(0, "None", "NONE"), new(1, "Global", "GLOBALLY"), new(2, "Per Faction", "PER_FACTION"), new(3, "Per Player", "PER_PLAYER")]),
        SessionSelect("LimitBlocksBy", "LimitBlocksBy", "combat", "Limit Blocks By", 160, [new(0, "Block Pair Name", "BlockPairName"), new(1, "Tag", "Tag")]),
        SessionText("BlockTypeLimits", "BlockTypeLimits", "combat", "Block Type World Limits", 170, QuasarConfigOptionKind.KeyValueText, "One limit per line as BlockSubtype=Limit. Values are written to Sandbox_config.sbc as Space Engineers block type limits.", "block limits block type limits pcu"),
        SessionBool("EnableShareInertiaTensor", "EnableShareInertiaTensor", "combat", "Enable Share Inertia Tensor", 180),
        SessionBool("EnableUnsafePistonImpulses", "EnableUnsafePistonImpulses", "combat", "Enable Unsafe Piston Impulses", 190),
        SessionBool("EnableUnsafeRotorTorques", "EnableUnsafeRotorTorques", "combat", "Enable Unsafe Rotor Torques", 200),
        SessionBool("EnableFriendlyFire", "EnableFriendlyFire", "combat", "Enable Friendly Fire", 210),

        SessionBool("CargoShipsEnabled", "CargoShipsEnabled", "npcs", "Cargo Ships Enabled", 10),
        SessionBool("EnableEncounters", "EnableEncounters", "npcs", "Enable Encounters", 20),
        SessionBool("EnableDrones", "EnableDrones", "npcs", "Enable Drones", 30),
        SessionInt("MaxDrones", "MaxDrones", "npcs", "Max Drones", 40, min: 0),
        SessionBool("EnableWolfs", "EnableWolfs", "npcs", "Enable Wolves", 50),
        SessionBool("EnableSpiders", "EnableSpiders", "npcs", "Enable Spiders", 60),
        SessionBool("EnableSunRotation", "EnableSunRotation", "npcs", "Enable Sun Rotation", 70),
        SessionDecimal("SunRotationIntervalMinutes", "SunRotationIntervalMinutes", "npcs", "Sun Rotation Interval (min)", 80, min: 0, step: 1),
        SessionBool("EnableOxygen", "EnableOxygen", "survival", "Enable Oxygen", 210),
        SessionBool("EnableOxygenPressurization", "EnableOxygenPressurization", "survival", "Enable Oxygen Pressurization", 220),
        SessionBool("EnableRadiation", "EnableRadiation", "survival", "Enable Radiation", 230, helperText: "Requires Oxygen Pressurization."),
        SessionDecimal("SolarRadiationIntensity", "SolarRadiationIntensity", "survival", "Solar Radiation Intensity", 240, min: 0, max: 100, step: 0.1, helperText: "Requires Oxygen Pressurization and Radiation."),
        SessionDecimal("FoodConsumptionRate", "FoodConsumptionRate", "survival", "Food Consumption Rate", 250, min: 0, max: 1, step: 0.01, helperText: "0 disables hunger."),
        SessionBool("EnableSurvivalBuffs", "EnableSurvivalBuffs", "survival", "Enable Survival Buffs", 290),
        SessionBool("EnableReducedStatsOnRespawn", "EnableReducedStatsOnRespawn", "survival", "Enable Reduced Stats On Respawn", 300),
        SessionBool("WeatherSystem", "WeatherSystem", "npcs", "Weather System", 110),
        SessionBool("WeatherLightingDamage", "WeatherLightingDamage", "npcs", "Weather Lightning Damage", 120, searchAliases: "lighting lightning"),
        RootInt("AsteroidAmount", "AsteroidAmount", "npcs", "Asteroid Amount", 125, min: 0),
        SessionBool("PredefinedAsteroids", "PredefinedAsteroids", "npcs", "Predefined Asteroids", 130),
        SessionInt("MaxPlanets", "MaxPlanets", "npcs", "Max Planets", 140, min: 0),
        SessionBool("EnableOrca", "EnableOrca", "npcs", "Enable ORCA", 150),
        SessionDecimal("ReputationDecayRate", "ReputationDecayRate", "npcs", "Reputation Decay Rate", 160, min: 0, max: 1, step: 0.01),
        SessionInt("GlobalEncounterTimer", "GlobalEncounterTimer", "npcs", "Global Encounter Timer (min)", 170, min: 1),
        SessionInt("GlobalEncounterCap", "GlobalEncounterCap", "npcs", "Global Encounter Cap", 180, min: 0, max: 5),
        SessionBool("GlobalEncounterEnableRemovalTimer", "GlobalEncounterEnableRemovalTimer", "npcs", "Global Encounter Removal", 190),
        SessionInt("GlobalEncounterMinRemovalTimer", "GlobalEncounterMinRemovalTimer", "npcs", "Global Encounter Removal Min (min)", 200, min: 20),
        SessionInt("GlobalEncounterMaxRemovalTimer", "GlobalEncounterMaxRemovalTimer", "npcs", "Global Encounter Removal Max (min)", 210, min: 21),
        SessionInt("GlobalEncounterRemovalTimeClock", "GlobalEncounterRemovalTimeClock", "npcs", "Global Encounter Removal Clock (min)", 220, min: 15, max: 60),
        SessionDecimal("EncounterDensity", "EncounterDensity", "npcs", "Random Encounter Density", 230, min: 0, max: 1, step: 0.01),
        SessionBool("EnablePlanetaryEncounters", "EnablePlanetaryEncounters", "npcs", "Enable Planetary Encounters", 240),
        SessionDecimal("PlanetaryEncounterTimerMin", "PlanetaryEncounterTimerMin", "npcs", "Planetary Encounter Timer Min (min)", 250, min: 1, max: 240, step: 1),
        SessionDecimal("PlanetaryEncounterTimerMax", "PlanetaryEncounterTimerMax", "npcs", "Planetary Encounter Timer Max (min)", 260, min: 1, max: 240, step: 1),
        SessionDecimal("PlanetaryEncounterTimerFirst", "PlanetaryEncounterTimerFirst", "npcs", "Planetary Encounter First Timer (min)", 270, min: 1, max: 240, step: 1),
        SessionInt("PlanetaryEncounterExistingStructuresRange", "PlanetaryEncounterExistingStructuresRange", "npcs", "Planetary Existing Structures Range", 280, min: 1000, max: 10000),
        SessionInt("PlanetaryEncounterAreaLockdownRange", "PlanetaryEncounterAreaLockdownRange", "npcs", "Planetary Area Lockdown Range", 290, min: 1000, max: 120000),
        SessionInt("PlanetaryEncounterDesiredSpawnRange", "PlanetaryEncounterDesiredSpawnRange", "npcs", "Planetary Desired Spawn Range", 300, min: 1000, max: 15000),
        SessionInt("PlanetaryEncounterPresenceRange", "PlanetaryEncounterPresenceRange", "npcs", "Planetary Presence Range", 310, min: 3000, max: 120000),
        SessionDecimal("PlanetaryEncounterDespawnTimeout", "PlanetaryEncounterDespawnTimeout", "npcs", "Planetary Despawn Timeout (min)", 320, min: 5, max: 1440, step: 1),

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
        SessionBool("EnableTrashSettingsPlatformOverride", "EnableTrashSettingsPlatformOverride", "trash", "Platform Trash Settings Override", 160),
        SessionInt("MaxCargoBags", "MaxCargoBags", "trash", "Max Cargo Bags", 170, min: 2, max: 1024),
        SessionInt("TrashCleanerCargoBagsMaxLiveTime", "TrashCleanerCargoBagsMaxLiveTime", "trash", "Max Cargo Bags Lifetime (min)", 180, min: 2, max: 1024),
        SessionBool("ScrapEnabled", "ScrapEnabled", "trash", "Enable Scrap Drops", 190),
        SessionBool("TemporaryContainers", "TemporaryContainers", "trash", "Enable Temporary Containers", 200),
        SessionBool("ResetForageableItems", "ResetForageableItems", "trash", "Reset Forageable Items", 210),
        SessionInt("ResetForageableItemsTimeM", "ResetForageableItemsTimeM", "trash", "Reset Forageable Items Time (min)", 220, min: 1),
        SessionInt("ResetForageableItemsDistance", "ResetForageableItemsDistance", "trash", "Reset Forageable Items Distance", 230, min: 1),

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
        SessionBool("GridStorageAllowsInventory", "GridStorageAllowsInventory", "economy", "Allow Items In Stored Grids", 150),
        SessionInt("GridStorageMaxPerPlayer", "GridStorageMaxPerPlayer", "economy", "Max Stored Grids", 160, min: 0, max: 100),
        SessionDecimal("GridStorageRetrievalTimeMaxMinutes", "GridStorageRetrievalTimeMaxMinutes", "economy", "Grid Storage Max Retrieval Time", 170, min: 0, max: 1440, step: 1),
        SessionDecimal("GridStorageRetrievalTimeMinMinutes", "GridStorageRetrievalTimeMinMinutes", "economy", "Grid Storage Min Retrieval Time", 180, min: 0, max: 100, step: 1),
        SessionDecimal("GridStorageRetrievalTimeMultiplier", "GridStorageRetrievalTimeMultiplier", "economy", "Grid Storage Retrieval Multiplier", 190, min: 0, max: 10, step: 0.1),
        SessionDecimal("GridStorageMinutesPerPcu", "GridStorageMinutesPerPCU", "economy", "Grid Storage Minutes Per PCU", 200, min: 0, max: 100, step: 0.001),
        SessionDecimal("GridStorageExpediteFactor", "GridStorageExpediteFactor", "economy", "Grid Storage Expedite Factor", 210, min: 0, max: 1, step: 0.01),
        SessionDecimal("GridStorageExpediteCostPerSecond", "GridStorageExpediteCostPerSecond", "economy", "Grid Storage Expedite Cost", 220, min: 0, max: 100000, step: 100),

        SessionBool("ResetOwnership", "ResetOwnership", "advanced", "Reset Ownership", 10),
        SessionBool("RealisticSound", "RealisticSound", "advanced", "Realistic Sound", 20),
        SessionInt("VoxelGeneratorVersion", "VoxelGeneratorVersion", "advanced", "Voxel Generator Version", 30, min: 0),
        SessionBool("ScenarioEditMode", "ScenarioEditMode", "advanced", "Scenario Edit Mode", 40),
        SessionBool("Scenario", "Scenario", "advanced", "Scenario", 50),
        SessionBool("CanJoinRunning", "CanJoinRunning", "advanced", "Can Join Running", 60),
        SessionBool("EnableIngameScripts", "EnableIngameScripts", "advanced", "Enable In-Game Scripts", 70),
        SessionInt("PhysicsIterations", "PhysicsIterations", "advanced", "Physics Iterations", 80, min: 1),
        SessionBool("ExperimentalMode", "ExperimentalMode", "advanced", "Experimental Mode", 90),
        SessionBool("AdaptiveSimulationQuality", "AdaptiveSimulationQuality", "advanced", "Adaptive Simulation Quality", 100),
        SessionInt("MinDropContainerRespawnTime", "MinDropContainerRespawnTime", "advanced", "Min Drop Container Respawn Time", 100, min: 0),
        SessionInt("MaxDropContainerRespawnTime", "MaxDropContainerRespawnTime", "advanced", "Max Drop Container Respawn Time", 110, min: 0),
        SessionDecimal("OptimalSpawnDistance", "OptimalSpawnDistance", "advanced", "Optimal Spawn Distance", 120, min: 0, step: 100),
        SessionBool("SimplifiedSimulation", "SimplifiedSimulation", "advanced", "Simplified Simulation", 130),
        SessionBool("EnableMatchComponent", "EnableMatchComponent", "advanced", "Enable Match Component", 140),
        SessionDecimal("PreMatchDuration", "PreMatchDuration", "advanced", "Pre-Match Duration", 150, min: 0, max: 60000, step: 1),
        SessionDecimal("MatchDuration", "MatchDuration", "advanced", "Match Duration", 160, min: 0, max: 60000, step: 1),
        SessionDecimal("PostMatchDuration", "PostMatchDuration", "advanced", "Post-Match Duration", 170, min: 0, max: 60000, step: 1),
        SessionBool("EnableTeamBalancing", "EnableTeamBalancing", "advanced", "Enable Team Balancing", 180),
        SessionBool("EnableTeamScoreCounters", "EnableTeamScoreCounters", "advanced", "Enable Team Score Counters", 190),
        SessionInt("MatchRestartWhenEmptyTime", "MatchRestartWhenEmptyTime", "advanced", "Match Restart When Empty (min)", 200, min: 0, max: 1440),
        SessionBool("EnableFactionVoiceChat", "EnableFactionVoiceChat", "advanced", "Enable Faction Voice Chat", 210),
        SessionInt("MaxProductionQueueLength", "MaxProductionQueueLength", "advanced", "Max Production Queue Length", 220, min: 0, max: 99999),
        SessionInt("PrefetchShapeRayLengthLimit", "PrefetchShapeRayLengthLimit", "advanced", "Prefetch Voxels Range Limit", 230, min: 0, max: 100000),
        SessionDecimal("EnemyTargetIndicatorDistance", "EnemyTargetIndicatorDistance", "advanced", "Enemy Target Indicator Distance", 240, min: 0, max: 1000, step: 1),
        SessionInt("BroadcastControllerMaxOfflineTransmitDistance", "BroadcastControllerMaxOfflineTransmitDistance", "advanced", "Offline Broadcast Controller Distance", 250, min: 0, max: 20000),
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

    private static QuasarConfigOptionDefinition SessionText(string propertyName, string elementName, string categoryKey, string label, int order, QuasarConfigOptionKind kind = QuasarConfigOptionKind.Text, string helperText = "", string searchAliases = "") =>
        new()
        {
            Scope = QuasarConfigOptionScope.Session,
            PropertyName = propertyName,
            ElementName = elementName,
            CategoryKey = categoryKey,
            Label = label,
            Kind = kind,
            Order = order,
            HelperText = helperText,
            SearchAliases = searchAliases,
        };
}
