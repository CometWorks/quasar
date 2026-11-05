using System.Security.Claims;
using System.Text.Json;

namespace Quasar.Plugin.Abstractions.Operations;

public enum QuasarOperationEffect
{
    Read,
    Change,
}

public enum QuasarReversal
{
    NotReversible,
    NothingToReverse,
    Undo,
}

public enum QuasarAgentAccess
{
    Allowed,
    Denied,
}

/// <summary>What an operation does, for the audit log, undo and agent principals.</summary>
public sealed record QuasarOperationMetadata
{
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinimumDeadline = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumDeadline = TimeSpan.FromSeconds(300);

    /// <summary>Defaults to Read for read and subscription operations, Change for JSON and upload operations.
    /// Every change is audited and an agent can only propose it.</summary>
    public QuasarOperationEffect? Effect { get; init; }

    /// <summary>Lowercase identifier such as <c>servers</c> or <c>config</c>; defaults to the first
    /// <c>.</c>-separated segment of the operation ID.</summary>
    public string? Area { get; init; }

    public QuasarReversal Reversal { get; init; } = QuasarReversal.NotReversible;

    /// <summary>Required exactly when <see cref="Reversal"/> is <see cref="QuasarReversal.Undo"/>.</summary>
    public IQuasarUndoHandler? Undo { get; init; }

    /// <summary>JSON operations only. Null means <see cref="DefaultDeadline"/>; 1 to 300 seconds.</summary>
    public TimeSpan? Deadline { get; init; }

    /// <summary>Denied puts the operation on the hard deny list for agent principals.</summary>
    public QuasarAgentAccess AgentAccess { get; init; } = QuasarAgentAccess.Allowed;

    /// <summary>One or two plain sentences for a person or a model: what the operation does, what it
    /// returns and when it applies. An assistant shows it as the tool description.</summary>
    public string? Description { get; init; }

    /// <summary>Checks the declared values and fills the defaults that depend on the operation.</summary>
    public QuasarOperationMetadata ResolveFor(QuasarOperationKind kind, string operationId)
    {
        if (Deadline is { } deadline && (deadline < MinimumDeadline || deadline > MaximumDeadline))
            throw new ArgumentOutOfRangeException(
                nameof(Deadline),
                deadline,
                "Operation deadlines must be between 1 and 300 seconds."
            );
        if ((Reversal == QuasarReversal.Undo) != (Undo is not null))
            throw new ArgumentException(
                "An undo handler is required exactly when the reversal is Undo."
            );
        var area = Area ?? operationId.Split('.')[0];
        if (!QuasarOperation.IsIdentifier(area))
            throw new ArgumentException("An operation area must be a lowercase identifier.");
        return this with
        {
            Effect =
                Effect
                ?? (
                    kind is QuasarOperationKind.Read or QuasarOperationKind.Subscription
                        ? QuasarOperationEffect.Read
                        : QuasarOperationEffect.Change
                ),
            Area = area,
        };
    }
}

/// <summary>Reverses one audited change from the undo data its invocation recorded.</summary>
public interface IQuasarUndoHandler
{
    /// <summary>Null when the current state is still what the action left; otherwise a short, readable difference.</summary>
    Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token);

    Task ReverseAsync(QuasarUndoContext context, CancellationToken token);
}

/// <param name="Data">What the invocation passed to <see cref="QuasarUndoRecorder.SetData"/>; undefined when it set none.</param>
/// <param name="BlobDirectory">The folder holding the entry's blobs, by the names given to <see cref="QuasarUndoRecorder.AddBlobAsync"/>.</param>
public sealed record QuasarUndoContext(
    IServiceProvider Services,
    ClaimsPrincipal User,
    JsonElement Data,
    string BlobDirectory
);

/// <summary>Collects a change operation's undo data during its invocation.</summary>
public sealed class QuasarUndoRecorder(string blobDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string BlobDirectory { get; } = blobDirectory;
    public JsonElement? Data { get; private set; }
    public bool HasBlobs { get; private set; }
    public bool Recorded => Data is not null || HasBlobs;

    /// <summary>The unique name of the server the action touched, for the audit filter.</summary>
    public string? Server { get; set; }

    /// <summary>Small values kept inline in the audit entry, such as before and after values.</summary>
    public void SetData(object value) =>
        Data = JsonSerializer.SerializeToElement(value, JsonOptions);

    /// <summary>Files kept beside the entry, such as a replaced file or a grid blueprint.</summary>
    public async Task AddBlobAsync(string name, Stream content, CancellationToken token)
    {
        if (
            string.IsNullOrWhiteSpace(name)
            || name != Path.GetFileName(name)
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        )
            throw new ArgumentException(
                "An undo blob name must be a plain file name.",
                nameof(name)
            );
        Directory.CreateDirectory(BlobDirectory);
        await using (
            var file = new FileStream(
                Path.Combine(BlobDirectory, name),
                FileMode.CreateNew,
                FileAccess.Write
            )
        )
            await content.CopyToAsync(file, token);
        HasBlobs = true;
    }
}
