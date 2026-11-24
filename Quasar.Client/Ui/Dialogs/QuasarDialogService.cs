using Microsoft.AspNetCore.Components;
using Quasar.Ui;

namespace Quasar.Client.Ui.Dialogs;

/// <summary>
/// Owns open dialog entries and completes their results when the native dialog
/// closes. The host component performs the JavaScript work.
/// </summary>
/// <remarks>
/// <para>
/// The service is the single source of truth for the dialog stack. It is
/// scoped to the browser application, so every page and dialog sees the same
/// stack. <see cref="QuasarDialogHost"/> subscribes to <see cref="Changed"/>
/// and renders one <see cref="QuasarDialogInstanceHost"/> per entry.
/// </para>
/// <para>
/// Closing is split into two phases. <see cref="RequestClose"/> only records
/// the requested result and raises <see cref="Changed"/>; the instance host then
/// asks JavaScript to close the native dialog. When the browser reports the
/// native <c>close</c> event, <see cref="FinishClose"/> removes the entry and
/// completes the caller's result exactly once. This avoids disposing a Blazor
/// component before its DOM element has finished closing.
/// </para>
/// <para>
/// All entry mutations happen under a lock. <see cref="Changed"/> is raised
/// outside the lock so a re-render cannot deadlock against a close request.
/// </para>
/// </remarks>
internal sealed class QuasarDialogService : IQuasarDialogService, IDisposable
{
    private readonly Lock _sync = new();
    private readonly List<QuasarDialogEntry> _entries = [];
    private bool _disposed;

    /// <summary>
    /// Raised when the open dialog set changes. The host re-renders on the
    /// Blazor synchronization context.
    /// </summary>
    public event Action? Changed;

    /// <inheritdoc />
    public IQuasarDialogReference Show<TComponent>(
        string? title = null,
        QuasarDialogParameters? parameters = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent =>
        ShowCore(typeof(TComponent), title, parameters, options, cancellationToken);

    /// <inheritdoc />
    public IQuasarDialogReference Show(
        Type componentType,
        string? title = null,
        QuasarDialogParameters? parameters = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ShowCore(componentType, title, parameters, options, cancellationToken);

    private QuasarDialogReference ShowCore(
        Type componentType,
        string? title = null,
        QuasarDialogParameters? parameters = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(componentType);
        if (!typeof(IComponent).IsAssignableFrom(componentType) || componentType.IsAbstract)
            throw new ArgumentException($"{componentType} is not a concrete Blazor component.", nameof(componentType));

        var id = Guid.CreateVersion7();
        var reference = new QuasarDialogReference(id);
        if (cancellationToken.IsCancellationRequested)
        {
            // The dialog never opens. Complete the result immediately so an
            // awaiting caller does not wait for a host that will not render.
            reference.Complete(QuasarDialogResult.Cancel());
            return reference;
        }

        var dialogParameters = parameters ?? new QuasarDialogParameters();

        // ComponentParameters is a mutable dictionary copy because Blazor's
        // DynamicComponent requires IDictionary<string, object>. The public
        // parameter set stays read-only for the caller.
        var entry = new QuasarDialogEntry
        {
            Id = id,
            ComponentType = componentType,
            Parameters = dialogParameters,
            ComponentParameters = dialogParameters
                .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase),
            Options = options ?? new QuasarDialogOptions(),
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Reference = reference,
        };
        entry.Instance = new QuasarDialogInstance(entry);
        reference.CloseRequested = result => RequestClose(entry, result);

        lock (_sync)
        {
            // Re-check under the lock: Dispose may have run between the first
            // check and here.
            ObjectDisposedException.ThrowIf(_disposed, this);
            _entries.Add(entry);
        }

        RaiseChanged();
        return reference;
    }

    /// <inheritdoc />
    public Task<IQuasarDialogReference> ShowAsync<TComponent>(
        string? title = null,
        QuasarDialogParameters? parameters = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent =>
        Task.FromResult<IQuasarDialogReference>(ShowCore(typeof(TComponent), title, parameters, options, cancellationToken));

    /// <inheritdoc />
    public Task<IQuasarDialogReference> ShowAsync(
        Type componentType,
        string? title = null,
        QuasarDialogParameters? parameters = null,
        QuasarDialogOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IQuasarDialogReference>(ShowCore(componentType, title, parameters, options, cancellationToken));

    /// <summary>An immutable snapshot of the open entries, oldest first.</summary>
    internal IReadOnlyList<QuasarDialogEntry> Entries
    {
        get
        {
            lock (_sync)
                return _entries.ToArray();
        }
    }

    /// <summary>
    /// Records a caller-requested close. The native close event is handled
    /// later by <see cref="FinishClose"/>.
    /// </summary>
    internal void RequestClose(QuasarDialogEntry entry, QuasarDialogResult? result)
    {
        lock (_sync)
        {
            // Ignore repeated requests from the close button, Escape, a backdrop
            // click, or navigation while a close is already in flight.
            if (!_entries.Contains(entry) || entry.CloseRequested)
                return;
            entry.RequestedResult = result ?? QuasarDialogResult.Cancel();
            entry.CloseRequested = true;
        }

        RaiseChanged();
    }

    /// <summary>
    /// Requests closure of every dialog whose
    /// <see cref="QuasarDialogOptions.CloseOnNavigation"/> is enabled. Called by
    /// the host on every location change.
    /// </summary>
    internal void RequestCloseOnNavigation()
    {
        QuasarDialogEntry[] closing;
        lock (_sync)
        {
            closing = _entries.Where(entry => entry.Options.CloseOnNavigation && !entry.CloseRequested).ToArray();
            foreach (var entry in closing)
            {
                entry.RequestedResult = QuasarDialogResult.Cancel();
                entry.CloseRequested = true;
            }
        }

        if (closing.Length > 0)
            RaiseChanged();
    }

    /// <summary>
    /// Called by the host after the native dialog close event (or after a
    /// JavaScript failure). Completes the result exactly once and removes the entry.
    /// </summary>
    /// <param name="id">Identifier of the dialog that finished closing.</param>
    internal void FinishClose(Guid id)
    {
        QuasarDialogEntry? entry;
        lock (_sync)
        {
            entry = _entries.FirstOrDefault(candidate => candidate.Id == id);
            if (entry is null)
                return;
            _entries.Remove(entry);
        }

        entry.Reference.Complete(entry.RequestedResult ?? QuasarDialogResult.Cancel());
        RaiseChanged();
    }

    /// <summary>
    /// Cancels every open dialog so no awaiting caller is left hanging when the
    /// browser application shuts down.
    /// </summary>
    public void Dispose()
    {
        QuasarDialogEntry[] entries;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = _entries.ToArray();
            _entries.Clear();
        }

        foreach (var entry in entries)
            entry.Reference.Complete(QuasarDialogResult.Cancel());
        Changed = null;
    }

    private void RaiseChanged() => Changed?.Invoke();
}
