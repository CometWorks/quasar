using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quasar.Models;

[JsonConverter(typeof(QuasarNetworkTypeJsonConverter))]
public enum QuasarNetworkType
{
    Steam,
    EOS,
}

public sealed class QuasarNetworkTypeJsonConverter : JsonConverter<QuasarNetworkType>
{
    public override QuasarNetworkType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return string.Equals(value, "EOS", StringComparison.OrdinalIgnoreCase)
            ? QuasarNetworkType.EOS
            : QuasarNetworkType.Steam;
    }

    public override void Write(Utf8JsonWriter writer, QuasarNetworkType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToConfigValue());
    }
}

public static class QuasarNetworkTypeExtensions
{
    public static string ToConfigValue(this QuasarNetworkType value) =>
        value == QuasarNetworkType.EOS ? "EOS" : "steam";
}

public sealed class QuasarConfigProfile
{
    public string ConfigProfileId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public QuasarWorldRootSettings RootSettings { get; set; } = new();

    public QuasarSessionSettings SessionSettings { get; set; } = new();

    public List<QuasarPluginSelection> Plugins { get; set; } = [];

    public List<QuasarModSelection> Mods { get; set; } = [];

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class QuasarDevFolderSelection
{
    public string Name { get; set; } = string.Empty;

    public string FolderPath { get; set; } = string.Empty;

    public string DataFile { get; set; } = string.Empty;   // manifest XML filename, relative to FolderPath

    public string PluginId { get; set; } = string.Empty;   // source folder name; carried into <LocalPlugin><Name> and <LocalFolderConfig><Id>

    public bool DebugBuild { get; set; } = true;

    /// <summary>
    /// The innermost folder name of <see cref="FolderPath"/>. This is the
    /// identity Magnetar uses for a dev-folder plugin: it is written to the
    /// source's <c>&lt;LocalPlugin&gt;&lt;Name&gt;</c> and the active profile's
    /// <c>&lt;LocalFolderConfig&gt;&lt;Id&gt;</c> (e.g. <c>se-test-plugin</c>),
    /// not the manifest's GUID <c>&lt;Id&gt;</c>.
    /// </summary>
    [JsonIgnore]
    public string SourceFolderName => GetSourceFolderName(FolderPath);

    public static string GetSourceFolderName(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return string.Empty;

        return Path.GetFileName(folderPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}

public sealed class QuasarWorldRootSettings
{
    public string ServerPassword { get; set; } = string.Empty;

    public bool CrossPlatform { get; set; }

    public int AsteroidAmount { get; set; } = 4;

    public bool VerboseNetworkLogging { get; set; }

    public bool PauseGameWhenEmpty { get; set; }

    public string MessageOfTheDay { get; set; } = string.Empty;

    public string MessageOfTheDayUrl { get; set; } = string.Empty;

    public bool AutoRestartEnabled { get; set; } = true;

    public int AutoRestartTimeInMin { get; set; }

    public bool AutoRestartSave { get; set; } = true;

    public bool AutoUpdateEnabled { get; set; }

    public int AutoUpdateCheckIntervalInMin { get; set; } = 10;

    public int AutoUpdateRestartDelayInMin { get; set; } = 15;

    public string AutoUpdateSteamBranch { get; set; } = string.Empty;

    public string ServerDescription { get; set; } = string.Empty;

    public double WatcherInterval { get; set; } = 30.0;

    public double WatcherSimulationSpeedMinimum { get; set; } = 0.05;

    public int ManualActionDelay { get; set; } = 5;

    public string ManualActionChatMessage { get; set; } = "Server will be shut down in {0} min(s).";

    public bool AutodetectDependencies { get; set; } = true;

    public bool SaveChatToLog { get; set; }

    public QuasarNetworkType NetworkType { get; set; } = QuasarNetworkType.Steam;

    public ulong GroupId { get; set; }

    public List<string> Administrators { get; set; } = [];

    public List<ulong> Reserved { get; set; } = [];

    public List<ulong> Banned { get; set; } = [];

    public bool ConsoleCompatibility { get; set; }

    public bool ChatAntiSpamEnabled { get; set; } = true;

    public int SameMessageTimeout { get; set; } = 30;

    public double SpamMessagesTime { get; set; } = 0.5;

    public int SpamMessagesTimeout { get; set; } = 60;
}

public sealed class QuasarSessionSettings
{
    public int GameMode { get; set; } = 1;

    public double InventorySizeMultiplier { get; set; } = 3.0;

    public double BlocksInventorySizeMultiplier { get; set; } = 1.0;

    public double AssemblerSpeedMultiplier { get; set; } = 3.0;

    public double AssemblerEfficiencyMultiplier { get; set; } = 3.0;

    public double RefinerySpeedMultiplier { get; set; } = 3.0;

    public int OnlineMode { get; set; } = 1;

    public int MaxPlayers { get; set; } = 30;

    public int MaxFloatingObjects { get; set; } = 100;

    public int TotalBotLimit { get; set; } = 32;

    public int MaxBackupSaves { get; set; } = 5;

    public int MaxGridSize { get; set; } = 50000;

    public int MaxBlocksPerPlayer { get; set; } = 100000;

    public int TotalPcu { get; set; } = 600000;

    public int PiratePcu { get; set; } = 25000;

    public int GlobalEncounterPcu { get; set; } = 25000;

    public int MaxFactionsCount { get; set; }

    public int BlockLimitsEnabled { get; set; }

    public bool EnableRemoteBlockRemoval { get; set; } = true;

    public int EnvironmentHostility { get; set; } = 1;

    public bool AutoHealing { get; set; } = true;

    public bool EnableCopyPaste { get; set; } = true;

    public bool WeaponsEnabled { get; set; } = true;

    public bool ShowPlayerNamesOnHud { get; set; } = true;

    public bool ThrusterDamage { get; set; } = true;

    public bool CargoShipsEnabled { get; set; } = true;

    public bool EnableSpectator { get; set; }

    public int WorldSizeKm { get; set; }

    public bool RespawnShipDelete { get; set; }

    public bool ResetOwnership { get; set; }

    public double WelderSpeedMultiplier { get; set; } = 2.0;

    public double GrinderSpeedMultiplier { get; set; } = 2.0;

    public bool RealisticSound { get; set; }

    public double HackSpeedMultiplier { get; set; } = 0.33;

    public bool PermanentDeath { get; set; }

    public int AutoSaveInMinutes { get; set; } = 5;

    public bool EnableSaving { get; set; } = true;

    public bool InfiniteAmmo { get; set; }

    public bool EnableContainerDrops { get; set; } = true;

    public double SpawnShipTimeMultiplier { get; set; }

    public double ProceduralDensity { get; set; }

    public int ProceduralSeed { get; set; }

    public bool DestructibleBlocks { get; set; } = true;

    public bool EnableIngameScripts { get; set; } = true;

    public int ViewDistance { get; set; } = 15000;

    public bool EnableToolShake { get; set; }

    public int VoxelGeneratorVersion { get; set; } = 4;

    public bool EnableOxygen { get; set; }

    public bool EnableOxygenPressurization { get; set; }

    public bool Enable3rdPersonView { get; set; } = true;

    public bool EnableEncounters { get; set; } = true;

    public bool EnableConvertToStation { get; set; } = true;

    public bool StationVoxelSupport { get; set; }

    public bool EnableSunRotation { get; set; } = true;

    public bool EnableRespawnShips { get; set; } = true;

    public bool EnableSpaceSuitRespawn { get; set; } = true;

    public double EnvironmentDamageMultiplier { get; set; } = 1.0;

    public double BackpackDespawnTimer { get; set; } = 5.0;

    public bool EnableRadiation { get; set; } = true;

    public double SolarRadiationIntensity { get; set; }

    public double FoodConsumptionRate { get; set; }

    public bool EnableSurvivalBuffs { get; set; } = true;

    public bool EnableReducedStatsOnRespawn { get; set; } = true;

    public bool ScenarioEditMode { get; set; }

    public bool Scenario { get; set; }

    public bool CanJoinRunning { get; set; }

    public int PhysicsIterations { get; set; } = 8;

    public double SunRotationIntervalMinutes { get; set; } = 120.0;

    public bool EnableJetpack { get; set; } = true;

    public bool SpawnWithTools { get; set; } = true;

    public bool EnableVoxelDestruction { get; set; } = true;

    public int MaxDrones { get; set; } = 5;

    public bool EnableDrones { get; set; } = true;

    public bool EnableWolfs { get; set; } = true;

    public bool EnableSpiders { get; set; }

    public double FloraDensityMultiplier { get; set; } = 1.0;

    public Dictionary<string, int> BlockTypeLimits { get; set; } = new()
    {
        ["Assembler"] = 24,
        ["Refinery"] = 24,
        ["Blast Furnace"] = 24,
        ["Antenna"] = 30,
        ["Drill"] = 30,
        ["InteriorTurret"] = 50,
        ["GatlingTurret"] = 50,
        ["MissileTurret"] = 50,
        ["ExtendedPistonBase"] = 50,
        ["MotorStator"] = 50,
        ["MotorAdvancedStator"] = 50,
        ["ShipWelder"] = 100,
        ["ShipGrinder"] = 150,
    };

    public bool EnableScripterRole { get; set; }

    public int MinDropContainerRespawnTime { get; set; } = 5;

    public int MaxDropContainerRespawnTime { get; set; } = 20;

    public bool EnableTurretsFriendlyFire { get; set; }

    public bool EnableSubgridDamage { get; set; }

    public int SyncDistance { get; set; } = 3000;

    public bool ExperimentalMode { get; set; }

    public bool AdaptiveSimulationQuality { get; set; } = true;

    public bool EnableVoxelHand { get; set; }

    public int RemoveOldIdentitiesH { get; set; }

    public bool TrashRemovalEnabled { get; set; } = true;

    public int StopGridsPeriodMin { get; set; } = 15;

    public int TrashFlagsValue { get; set; } = 7706;

    public int AfkTimeoutMin { get; set; }

    public int BlockCountThreshold { get; set; } = 20;

    public double PlayerDistanceThreshold { get; set; } = 500.0;

    public int OptimalGridCount { get; set; }

    public double PlayerInactivityThreshold { get; set; }

    public int PlayerCharacterRemovalThreshold { get; set; } = 15;

    public bool VoxelTrashRemovalEnabled { get; set; }

    public double VoxelPlayerDistanceThreshold { get; set; } = 5000.0;

    public double VoxelGridDistanceThreshold { get; set; } = 5000.0;

    public int VoxelAgeThreshold { get; set; } = 24;

    public bool EnableResearch { get; set; }

    public bool EnableGoodBotHints { get; set; } = true;

    public double OptimalSpawnDistance { get; set; } = 16000.0;

    public bool EnableAutorespawn { get; set; } = true;

    public bool EnableBountyContracts { get; set; } = true;

    public bool EnableSupergridding { get; set; }

    public bool EnableEconomy { get; set; }

    public double DepositsCountCoefficient { get; set; } = 2.0;

    public double DepositSizeDenominator { get; set; } = 30.0;

    public bool WeatherSystem { get; set; } = true;

    public bool WeatherLightingDamage { get; set; }

    public double HarvestRatioMultiplier { get; set; } = 1.0;

    public int TradeFactionsCount { get; set; } = 10;

    public double StationsDistanceInnerRadius { get; set; } = 5000000.0;

    public double StationsDistanceOuterRadiusStart { get; set; } = 5000000.0;

    public double StationsDistanceOuterRadiusEnd { get; set; } = 10000000.0;

    public int EconomyTickInSeconds { get; set; } = 300;

    public int NpcGridClaimTimeLimit { get; set; } = 120;

    public bool SimplifiedSimulation { get; set; }

    public bool EnablePcuTrading { get; set; } = true;

    public bool FamilySharing { get; set; } = true;

    public bool EnableSelectivePhysicsUpdates { get; set; }

    public bool PredefinedAsteroids { get; set; } = true;

    public bool UseConsolePcu { get; set; }

    public int MaxPlanets { get; set; } = 99;

    public bool OffensiveWordsFiltering { get; set; }

    public int BlueprintShareTimeout { get; set; } = 30;

    public bool BlueprintShare { get; set; } = true;

    public int LimitBlocksBy { get; set; }

    public bool EnableMatchComponent { get; set; }

    public double PreMatchDuration { get; set; }

    public double MatchDuration { get; set; }

    public double PostMatchDuration { get; set; }

    public bool EnableFriendlyFire { get; set; } = true;

    public bool EnableTeamBalancing { get; set; }

    public double CharacterSpeedMultiplier { get; set; } = 1.0;

    public bool EnableRecoil { get; set; } = true;

    public bool EnableGamepadAimAssist { get; set; }

    public bool EnableTeamScoreCounters { get; set; } = true;

    public int MatchRestartWhenEmptyTime { get; set; }

    public bool EnableFactionVoiceChat { get; set; }

    public bool EnableOrca { get; set; } = true;

    public int MaxProductionQueueLength { get; set; } = 50;

    public long PrefetchShapeRayLengthLimit { get; set; } = 15000;

    public double EnemyTargetIndicatorDistance { get; set; } = 20.0;

    public bool EnableTrashSettingsPlatformOverride { get; set; } = true;

    public int MinimumWorldSize { get; set; }

    public int MaxCargoBags { get; set; } = 100;

    public int TrashCleanerCargoBagsMaxLiveTime { get; set; } = 30;

    public bool ScrapEnabled { get; set; } = true;

    public int BroadcastControllerMaxOfflineTransmitDistance { get; set; } = 200;

    public bool TemporaryContainers { get; set; } = true;

    public int GlobalEncounterTimer { get; set; } = 15;

    public int GlobalEncounterCap { get; set; } = 1;

    public bool GlobalEncounterEnableRemovalTimer { get; set; } = true;

    public int GlobalEncounterMinRemovalTimer { get; set; } = 90;

    public int GlobalEncounterMaxRemovalTimer { get; set; } = 180;

    public int GlobalEncounterRemovalTimeClock { get; set; } = 30;

    public double EncounterDensity { get; set; } = 0.35;

    public bool EnablePlanetaryEncounters { get; set; } = true;

    public double PlanetaryEncounterTimerMin { get; set; } = 15.0;

    public double PlanetaryEncounterTimerMax { get; set; } = 30.0;

    public double PlanetaryEncounterTimerFirst { get; set; } = 5.0;

    public int PlanetaryEncounterExistingStructuresRange { get; set; } = 7000;

    public int PlanetaryEncounterAreaLockdownRange { get; set; } = 10000;

    public int PlanetaryEncounterDesiredSpawnRange { get; set; } = 6000;

    public int PlanetaryEncounterPresenceRange { get; set; } = 20000;

    public double PlanetaryEncounterDespawnTimeout { get; set; } = 120.0;

    public int MaxHudChatMessageCount { get; set; } = 100;

    public bool EnableShareInertiaTensor { get; set; }

    public bool EnableUnsafePistonImpulses { get; set; }

    public bool EnableUnsafeRotorTorques { get; set; }

    public bool ResetForageableItems { get; set; } = true;

    public int ResetForageableItemsTimeM { get; set; } = 30;

    public int ResetForageableItemsDistance { get; set; } = 3000;

    public double ReputationDecayRate { get; set; } = 0.5;

    public bool GridStorageAllowsInventory { get; set; }

    public int GridStorageMaxPerPlayer { get; set; } = 100;

    public double GridStorageRetrievalTimeMaxMinutes { get; set; } = 30.0;

    public double GridStorageRetrievalTimeMinMinutes { get; set; } = 2.0;

    public double GridStorageRetrievalTimeMultiplier { get; set; } = 1.0;

    public double GridStorageMinutesPerPcu { get; set; } = 0.001;

    public double GridStorageExpediteFactor { get; set; } = 0.5;

    public double GridStorageExpediteCostPerSecond { get; set; } = 1000.0;
}

public sealed class QuasarPluginSelection
{
    public string PluginId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string SelectedVersion { get; set; } = string.Empty;
}

public sealed class QuasarModSelection
{
    public long WorkshopId { get; set; }

    public string DisplayName { get; set; } = string.Empty;
}

public sealed class QuasarPluginCatalogEntry
{
    public string PluginId { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Tooltip { get; set; } = string.Empty;

    public string Runtimes { get; set; } = string.Empty;

    public string SourceRepo { get; set; } = string.Empty;

    public string ManifestRepo { get; set; } = string.Empty;

    public string ManifestBranch { get; set; } = string.Empty;

    public string ManifestFile { get; set; } = string.Empty;

    public bool Hidden { get; set; }

    public bool IsLocalDevFolder { get; set; }
}
