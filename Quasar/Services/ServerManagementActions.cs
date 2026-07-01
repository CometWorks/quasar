using System.Text.RegularExpressions;
using Magnetar.Protocol.Runtime;
using MudBlazor;
using Quasar.Components.Pages;
using Quasar.Models;

namespace Quasar.Services;

public sealed class ServerManagementActions(
    DedicatedServerCatalog serverCatalog,
    DedicatedServerSupervisor supervisor,
    WebServiceOptions options,
    AgentRegistry registry,
    QuasarWorldTemplateCatalog worldTemplates,
    IDialogService dialogService,
    ISnackbar snackbar)
{
    private bool _creatingWorldTemplate;

    private enum CloneWorldMode
    {
        CopyWorld,
        KeepSelectedWorld,
    }

    public async Task OpenConsoleDialogAsync(string uniqueName)
    {
        var parameters = new DialogParameters
        {
            [nameof(ServerConsoleDialog.UniqueName)] = uniqueName,
        };

        var dialogOptions = new DialogOptions
        {
            CloseOnEscapeKey = true,
            FullWidth = true,
            MaxWidth = MaxWidth.Large,
        };

        await dialogService.ShowAsync<ServerConsoleDialog>($"Console — {uniqueName}", parameters, dialogOptions);
    }

    public async Task OpenCreateDialogAsync()
    {
        var definition = await ShowEditorDialogAsync(CreateBlank(), isEditing: false, isClone: false);
        if (definition is null)
            return;

        await SaveDefinitionAsync(definition, "Server created.");
    }

    public async Task OpenEditDialogAsync(DedicatedServerDefinition definition)
    {
        var updated = await ShowEditorDialogAsync(definition, isEditing: true, isClone: false);
        if (updated is null)
            return;

        await SaveDefinitionAsync(updated, "Server saved.");
    }

    public async Task OpenCloneDialogAsync(DedicatedServerDefinition definition)
    {
        var cloned = definition.Clone();
        cloned.DisplayName = NormalizeWhitespace($"{GetDisplayName(definition)} Copy");
        cloned.UniqueName = MakeCopyIdentifier(definition.UniqueName);
        cloned.OriginalUniqueName = string.Empty;
        cloned.GoalState = DedicatedServerGoalState.Off;
        cloned.AutoStart = false;
        cloned.ServerPort = AllocateNextPort();
        cloned.DedicatedServerAppDataPath = string.Empty;
        cloned.MagnetarAppDataPath = string.Empty;
        cloned.WorldPath = string.Empty;
        cloned.WorldSaveName = string.Empty;
        cloned.ConfigFilePath = string.Empty;

        var created = await ShowEditorDialogAsync(cloned, isEditing: false, isClone: true);
        if (created is null)
            return;

        if (!EnsureCloneUsesIndependentPaths(definition, created))
            return;

        var cloneWorldMode = await ChooseCloneWorldModeAsync(definition);
        if (cloneWorldMode is null)
            return;

        if (!await SaveDefinitionAsync(created, string.Empty))
            return;

        var saved = serverCatalog.GetServer(created.UniqueName) ?? created;
        try
        {
            var worldMessage = cloneWorldMode == CloneWorldMode.CopyWorld
                ? await CopyCloneWorldStateAsync(definition, saved)
                : KeepCloneWorldState(definition, saved);
            snackbar.Add($"Server cloned. {worldMessage}", Severity.Success);
        }
        catch (OperationCanceledException exception)
        {
            snackbar.Add($"Server cloned; {exception.Message}", Severity.Info);
        }
        catch (Exception exception)
        {
            snackbar.Add($"Server cloned, but world preparation failed: {exception.Message}", Severity.Error);
        }
    }

    public async Task CreateWorldTemplateAsync(DedicatedServerDefinition definition)
    {
        if (!CanCreateWorldTemplate(definition))
        {
            snackbar.Add("Stop the server before creating a world template from its current state.", Severity.Warning);
            return;
        }

        var worldPath = definition.GetWorldSavePath();
        if (string.IsNullOrWhiteSpace(worldPath) || !Directory.Exists(worldPath))
        {
            snackbar.Add($"World save not found for '{definition.UniqueName}'.", Severity.Error);
            return;
        }

        if (!File.Exists(Path.Combine(worldPath, "Sandbox.sbc")))
        {
            snackbar.Add($"World save '{worldPath}' does not contain Sandbox.sbc.", Severity.Error);
            return;
        }

        var defaultName = $"{GetDisplayName(definition)} Snapshot {DateTime.Now:yyyy-MM-dd HHmm}";
        var parameters = new DialogParameters
        {
            [nameof(WorldTemplateFromServerDialog.DefaultName)] = defaultName,
            [nameof(WorldTemplateFromServerDialog.DefaultDescription)] = $"Created from stopped server '{definition.UniqueName}'.",
            [nameof(WorldTemplateFromServerDialog.WorldPath)] = worldPath,
        };

        var dialog = await dialogService.ShowAsync<WorldTemplateFromServerDialog>(
            "Create World Template",
            parameters,
            new DialogOptions { CloseOnEscapeKey = true, FullWidth = true, MaxWidth = MaxWidth.Small });

        var result = await dialog.Result;
        if (result is null || result.Canceled || result.Data is not WorldTemplateFromServerDialog.TemplateRequest request)
            return;

        _creatingWorldTemplate = true;
        try
        {
            var template = await worldTemplates.ImportAsync(request.Name, request.Description, worldPath);
            snackbar.Add($"World template '{template.Name}' created.", Severity.Success);
        }
        catch (Exception exception)
        {
            snackbar.Add(exception.Message, Severity.Error);
        }
        finally
        {
            _creatingWorldTemplate = false;
        }
    }

    public async Task DeleteAsync(string uniqueName)
    {
        if (IsRunning(uniqueName))
        {
            snackbar.Add("Stop the server before deleting its definition.", Severity.Warning);
            return;
        }

        var folder = MagnetarPaths.GetQuasarServerDirectory(uniqueName);

        var confirmParameters = new DialogParameters
        {
            ["Slug"] = uniqueName,
            ["FolderPath"] = folder,
            ["Confirm"] = true,
        };
        var confirmDialog = await dialogService.ShowAsync<ServerDeleteDialog>(
            "Delete server?",
            confirmParameters,
            new DialogOptions { CloseOnEscapeKey = true, FullWidth = true, MaxWidth = MaxWidth.Small });
        var confirmResult = await confirmDialog.Result;
        if (confirmResult is null || confirmResult.Canceled)
            return;

        await serverCatalog.DeleteAsync(uniqueName);
        registry.PruneDisconnectedByUniqueName(uniqueName);

        var removedParameters = new DialogParameters
        {
            ["Slug"] = uniqueName,
            ["FolderPath"] = folder,
            ["Confirm"] = false,
        };
        await dialogService.ShowAsync<ServerDeleteDialog>(
            "Server removed",
            removedParameters,
            new DialogOptions { CloseOnEscapeKey = true, FullWidth = true, MaxWidth = MaxWidth.Small });
    }

    public bool CanCreateWorldTemplate(DedicatedServerDefinition definition)
    {
        if (_creatingWorldTemplate || definition.GoalState != DedicatedServerGoalState.Off)
            return false;

        var state = GetRuntime(definition.UniqueName)?.State ?? DedicatedServerProcessState.Stopped;
        return state == DedicatedServerProcessState.Stopped;
    }

    private async Task<DedicatedServerDefinition?> ShowEditorDialogAsync(DedicatedServerDefinition definition, bool isEditing, bool isClone)
    {
        var parameters = new DialogParameters
        {
            [nameof(ServerEditorDialog.Definition)] = definition.Clone(),
            [nameof(ServerEditorDialog.IsEditing)] = isEditing,
            [nameof(ServerEditorDialog.IsClone)] = isClone,
            [nameof(ServerEditorDialog.UniqueNameLocked)] = isEditing && IsRunning(definition.UniqueName),
        };

        var dialogOptions = new DialogOptions
        {
            CloseOnEscapeKey = true,
            FullWidth = true,
            MaxWidth = MaxWidth.ExtraLarge,
        };

        var title = isEditing
            ? "Edit Server"
            : isClone
                ? "Clone Server"
                : "Create Server";
        var dialog = await dialogService.ShowAsync<ServerEditorDialog>(title, parameters, dialogOptions);
        var result = await dialog.Result;
        if (result is null || result.Canceled || result.Data is not DedicatedServerDefinition updated)
            return null;

        return updated;
    }

    private async Task<CloneWorldMode?> ChooseCloneWorldModeAsync(DedicatedServerDefinition source)
    {
        var sourceRunning = IsRunning(source.UniqueName);
        var message = sourceRunning
            ? $"Clone '{GetDisplayName(source)}' with its current world state? The source is running, so Quasar will copy from the latest Space Engineers Backup snapshot instead of the live world folder. Choose 'Keep Save' to use the save already selected in the editor."
            : $"Clone '{GetDisplayName(source)}' with its current world state? Choose 'Keep Save' to use the save already selected in the editor.";

        var result = await dialogService.ShowMessageBoxAsync(
            "Clone world state?",
            message,
            yesText: "Copy World",
            noText: "Keep Save",
            cancelText: "Cancel");

        if (result is null)
            return null;

        return result.Value ? CloneWorldMode.CopyWorld : CloneWorldMode.KeepSelectedWorld;
    }

    private async Task<string> CopyCloneWorldStateAsync(DedicatedServerDefinition source, DedicatedServerDefinition target)
    {
        EnsureCloneStorageIsIndependent(source, target);
        await ConfirmExistingCloneWorldPathDeleteAsync(target, "copy the source world into the clone");

        var sourceWorldPath = source.GetWorldSavePath();
        if (string.IsNullOrWhiteSpace(sourceWorldPath) || !Directory.Exists(sourceWorldPath))
            throw new InvalidOperationException($"Source world save not found for '{source.UniqueName}'.");

        if (IsRunning(source.UniqueName))
        {
            var backupPath = FindLatestWorldBackupDirectory(sourceWorldPath);
            if (backupPath is null)
                throw new InvalidOperationException($"No Space Engineers Backup snapshot exists under '{Path.Combine(sourceWorldPath, "Backup")}'. Stop the source server or let it create a backup before cloning world state.");

            await CopyWorldDirectoryAsync(backupPath, target.GetWorldSavePath());
            return $"World copied from latest Backup snapshot '{Path.GetFileName(backupPath)}'.";
        }

        await CopyStoppedWorldStateAsync(sourceWorldPath, target);
        return "World copied from the stopped source server.";
    }

    private string KeepCloneWorldState(DedicatedServerDefinition source, DedicatedServerDefinition target)
    {
        EnsureCloneStorageIsIndependent(source, target);

        return "Selected clone world save kept unchanged.";
    }

    private async Task CopyStoppedWorldStateAsync(string sourceWorldPath, DedicatedServerDefinition target)
    {
        if (!File.Exists(Path.Combine(sourceWorldPath, "Sandbox.sbc")))
            throw new InvalidOperationException($"Source world save '{sourceWorldPath}' does not contain Sandbox.sbc.");

        await CopyWorldDirectoryAsync(sourceWorldPath, target.GetWorldSavePath());
    }

    private static string? FindLatestWorldBackupDirectory(string worldPath)
    {
        var backupDirectory = Path.Combine(worldPath, "Backup");
        if (!Directory.Exists(backupDirectory))
            return null;

        return Directory
            .EnumerateDirectories(backupDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "Sandbox.sbc")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static Task CopyWorldDirectoryAsync(string sourceWorldPath, string targetWorldPath) =>
        Task.Run(() =>
        {
            if (!string.IsNullOrWhiteSpace(targetWorldPath) && Directory.Exists(targetWorldPath))
                Directory.Delete(targetWorldPath, recursive: true);

            CopyDirectory(sourceWorldPath, targetWorldPath, ShouldSkipCopiedWorldFile);
        });

    private async Task ConfirmExistingCloneWorldPathDeleteAsync(DedicatedServerDefinition target, string action)
    {
        var targetWorldPath = target.GetWorldSavePath();
        if (string.IsNullOrWhiteSpace(targetWorldPath) || !Directory.Exists(targetWorldPath))
            return;

        var confirmed = await ConfirmDestructiveActionAsync(
            "Delete existing clone world folder?",
            $"The clone's world save already exists: {targetWorldPath}. Quasar must delete that folder to {action}.",
            "Delete Folder");

        if (!confirmed)
            throw new OperationCanceledException("Clone world preparation canceled.");
    }

    private bool EnsureCloneUsesIndependentPaths(DedicatedServerDefinition source, DedicatedServerDefinition clone)
    {
        if (PathsEqual(source.DedicatedServerAppDataPath, clone.DedicatedServerAppDataPath))
        {
            snackbar.Add("Clone DS app-data path matches the source. Clear the path override or choose a different folder.", Severity.Error);
            return false;
        }

        if (PathsEqual(source.GetWorldSavePath(), clone.GetWorldSavePath()))
        {
            snackbar.Add("Clone world save path matches the source. Choose a different save.", Severity.Error);
            return false;
        }

        if (PathsEqual(source.ConfigFilePath, clone.ConfigFilePath))
        {
            snackbar.Add("Clone rendered config path matches the source. Clear the path override or choose a different file.", Severity.Error);
            return false;
        }

        return true;
    }

    private static void EnsureCloneStorageIsIndependent(DedicatedServerDefinition source, DedicatedServerDefinition target)
    {
        if (PathsEqual(source.DedicatedServerAppDataPath, target.DedicatedServerAppDataPath))
            throw new InvalidOperationException("Clone DS app-data path still matches the source.");

        if (PathsEqual(source.GetWorldSavePath(), target.GetWorldSavePath()))
            throw new InvalidOperationException("Clone world save path still matches the source.");

        if (PathsEqual(source.ConfigFilePath, target.ConfigFilePath))
            throw new InvalidOperationException("Clone rendered config path still matches the source.");
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory, Func<string, bool> shouldSkipFile)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if (shouldSkipFile(file))
                continue;

            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destination = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static bool ShouldSkipCopiedWorldFile(string path) =>
        Path.GetFileName(path).StartsWith("Sandbox_config.sbc", StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.GetFullPath(left.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private async Task<bool> SaveDefinitionAsync(DedicatedServerDefinition definition, string successMessage)
    {
        var previousUniqueName = definition.OriginalUniqueName;
        if (!string.IsNullOrWhiteSpace(previousUniqueName) &&
            !string.Equals(previousUniqueName, definition.UniqueName, StringComparison.OrdinalIgnoreCase) &&
            IsRunning(previousUniqueName))
        {
            snackbar.Add("Stop the server before renaming it.", Severity.Warning);
            return false;
        }

        try
        {
            await serverCatalog.UpsertAsync(definition);
            if (!string.IsNullOrWhiteSpace(successMessage))
                snackbar.Add(successMessage, Severity.Success);
            return true;
        }
        catch (Exception exception)
        {
            snackbar.Add(exception.Message, Severity.Error);
            return false;
        }
    }

    private async Task<bool> ConfirmDestructiveActionAsync(string title, string message, string yesText)
    {
        var confirmed = await dialogService.ShowMessageBoxAsync(
            title,
            message,
            yesText: yesText,
            cancelText: "Cancel");

        return confirmed == true;
    }

    private bool IsRunning(string uniqueName)
    {
        var state = GetRuntime(uniqueName)?.State ?? DedicatedServerProcessState.Stopped;
        return state is DedicatedServerProcessState.Starting
            or DedicatedServerProcessState.Running
            or DedicatedServerProcessState.Restarting
            or DedicatedServerProcessState.Stopping;
    }

    private DedicatedServerRuntimeSnapshot? GetRuntime(string uniqueName) =>
        supervisor.GetSnapshots().FirstOrDefault(snapshot => string.Equals(snapshot.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase));

    private string MakeCopyIdentifier(string uniqueName)
    {
        var baseName = string.IsNullOrWhiteSpace(uniqueName) ? "server" : uniqueName.Trim();
        var candidate = $"{baseName}-copy";
        var used = serverCatalog.GetServers()
            .Select(server => server.UniqueName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suffix = 2;

        while (used.Contains(candidate))
            candidate = $"{baseName}-copy-{suffix++}";

        return candidate;
    }

    private int AllocateNextPort()
    {
        var used = serverCatalog.GetServers()
            .Select(server => server.ServerPort)
            .Where(port => port > 0)
            .ToHashSet();

        var port = 27016;
        while (used.Contains(port) && port < 65535)
            port++;

        return port;
    }

    private DedicatedServerDefinition CreateBlank()
    {
        var healthMonitoringEnabled = !options.DisableServerHealthMonitoring;
        return new DedicatedServerDefinition
        {
            GoalState = DedicatedServerGoalState.Off,
            ServerPort = AllocateNextPort(),
            ServerIP = "0.0.0.0",
            EnableHealthMonitoring = healthMonitoringEnabled,
            AutoRestartOnUnhealthy = healthMonitoringEnabled,
            AgentStartupGraceSeconds = 180,
            AgentAttachRetryAttempts = DedicatedServerDefinition.DefaultAgentAttachRetryAttempts,
            AgentAttachRetryDelaySeconds = DedicatedServerDefinition.DefaultAgentAttachRetryDelaySeconds,
            AgentHeartbeatTimeoutSeconds = 20,
            SimulationProgressWindowSeconds = 30,
            MinimumSimulationProgressScore = 0.05f,
            WarnAfterUptimeHours = 12,
            RestartDelaySeconds = 5,
            RestartOnCrash = true,
        };
    }

    private static string GetDisplayName(DedicatedServerDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.UniqueName : definition.DisplayName;

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");
}
