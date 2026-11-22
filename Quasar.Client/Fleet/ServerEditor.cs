using Quasar.Models;

namespace Quasar.Client.Fleet;

public sealed record EditorSelection(string UniqueName = "", bool IsClone = false, string InitialProfile = "", string InitialTemplate = "");
public sealed record ServerEditorSetup(EditableServerDefinition Definition, DateTimeOffset? Revision, bool Active, bool Windows, int Cores,
    ExistingServer[] Servers, FleetProfile[] Profiles, WorldTemplate[] Templates, bool EditProfiles);
public sealed record ExistingServer(string UniqueName, int ServerPort);
public sealed record ServerPathInput(string UniqueName, string DedicatedServerAppDataPath, string MagnetarAppDataPath,
    string WorldPath, string WorldSaveName, string ConfigFilePath, string ExecutablePath, string WorkingDirectory, string ConfigProfileId, bool DisableImplicitMagnetarModLoad, string OriginalName = "")
{
    public static ServerPathInput From(EditableServerDefinition d) => new(d.UniqueName, d.DedicatedServerAppDataPath, d.MagnetarAppDataPath,
        d.WorldPath, d.WorldSaveName, d.ConfigFilePath, d.ExecutablePath, d.WorkingDirectory, d.ConfigProfileId, d.DisableImplicitMagnetarModLoad, d.OriginalUniqueName);
}
public sealed record ServerEditorPaths(string Saves, string Selected, string DefaultSaves, string DefaultConfig, string DefaultDs, string DefaultMagnetar,
    bool External, EditorSave[] Available, string[] MissingMods);
public sealed record EditorSave(string Name, string DisplayName, bool Valid);
public sealed record ServerEditorCommit(EditorSelection Source, DateTimeOffset? Revision, EditableServerDefinition Definition,
    bool CopyWorld, string? ReplaceWorld);
public sealed record SaveFromTemplate(ServerPathInput Path, string Template);
public sealed record EditorQuickProfile(string Name, string Description);
