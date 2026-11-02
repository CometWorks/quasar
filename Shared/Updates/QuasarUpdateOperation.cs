using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;

namespace Quasar.Updates;

[JsonConverter(typeof(JsonStringEnumConverter<UpdateStage>))]
public enum UpdateStage { Queued, Downloading, Verified, Ready, Restarting, Healthy, Failed, Abandoned }

public sealed record QuasarUpdateOperation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Version { get; init; } = "";
    public UpdateStage Stage { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string PackageDirectory { get; init; } = "";
    public QuasarActiveReleasePointer? WebRelease { get; init; }
    public string WorkerVersion { get; init; } = "";
    public string WebStageDirectory { get; init; } = "";
    public string ResolvedSettings { get; init; } = "";
    public string PreviousVersion { get; init; } = "";
    public bool RestorePrevious { get; init; }
    public bool ActivateWhenReady { get; init; } = true;
    public bool RestartOnly { get; init; }
    public bool NeedsBootstrap { get; init; } = true;
    public int HealthyProcessId { get; init; }
    public int HealthyUpdaterProcessId { get; init; }
    public DateTimeOffset? HealthyProcessStartedAtUtc { get; init; }
    // Legacy Bootstrap recovery records retain their two-process acknowledgements.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int HealthyBootstrapPid { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int HealthyWorkerPid { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? HealthyBootstrapStartedAtUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? HealthyWorkerStartedAtUtc { get; init; }
}

// One durable intent/outcome, shared by UI, local CLI and Bootstrap. Mutations
// require the existing cross-process maintenance lease. Never infer success from
// a request disappearing, a version pointer, or an executable merely being present.
internal static class QuasarUpdateJournal
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static string PathName => Path.Combine(MagnetarPaths.GetQuasarUpdatesDirectory(), "operation.json");
    internal static QuasarUpdateOperation? Read() => File.Exists(PathName)
        ? JsonSerializer.Deserialize<QuasarUpdateOperation>(ReadText(PathName), JsonOptions)
          ?? throw new InvalidDataException($"Invalid update journal: {PathName}") : null;

    internal static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static void Write(QuasarUpdateOperation operation) => WriteJson(PathName, operation with { UpdatedAtUtc = DateTimeOffset.UtcNow });

    internal static void WriteJson<T>(string path, T value) => WriteText(path, JsonSerializer.Serialize(value, JsonOptions));

    internal static void WriteText(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(value);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            UpdateDurability.Publish(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool IsVersion(string version) => System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?$");

    internal static QuasarUpdateOperation Submit(string? version, bool retry, bool stageOnly = false)
    {
        var previous = Read();
        if (File.Exists(Path.Combine(MagnetarPaths.GetQuasarUpdatesDirectory(), "application-maintenance")))
            throw new InvalidOperationException("The updater is still replacing or recovering Quasar. Check update --status before retrying.");
        if (retry)
        {
            if (previous is null || previous.Stage is not (UpdateStage.Failed or UpdateStage.Ready))
                throw new InvalidOperationException("No failed or staged update to retry. Use update --status.");
            version = previous.Version;
        }
        version = QuasarReleaseVersion.Normalize(version ?? "");
        if (!IsVersion(version))
            throw new ArgumentException("Specify a release version, for example --version 1.4.0.");
        if (previous is not null && previous.Stage != UpdateStage.Abandoned && !retry && string.Equals(previous.Version, version, StringComparison.OrdinalIgnoreCase))
        {
            if (previous.Stage == UpdateStage.Ready && !previous.ActivateWhenReady && !stageOnly)
            {
                previous = previous with { ActivateWhenReady = true, Message = "Staged release selected for activation; waiting for updater service verification." };
                Write(previous);
            }
            return previous;
        }
        if (previous?.Stage is UpdateStage.Queued or UpdateStage.Downloading or UpdateStage.Verified or UpdateStage.Restarting)
            throw new InvalidOperationException($"Update {previous.Id} to {previous.Version} is still {previous.Stage}. Use update --status.");
        if (File.Exists(MagnetarPaths.GetQuasarBootstrapUpdateRequestPath()))
            throw new InvalidOperationException("A Bootstrap recovery request is pending. Check Updates before starting another operation.");
        var operation = new QuasarUpdateOperation { Version = version, ActivateWhenReady = !stageOnly, ResolvedSettings = retry ? previous?.ResolvedSettings ?? "" : "", Stage = UpdateStage.Queued, Message = "Waiting for the running Quasar update service." };
        Write(operation);
        return operation;
    }

    internal static void Fail(string message)
    {
        if (Read() is { } operation && operation.Stage is not (UpdateStage.Healthy or UpdateStage.Abandoned))
            Write(operation with { Stage = UpdateStage.Failed, Message = message });
    }
}
