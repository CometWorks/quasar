using Quasar.Ui;

namespace Quasar.Client.Ui.Toasts;

/// <summary>
/// Tracks live toasts and runs their auto-dismiss timers.
/// </summary>
/// <remarks>
/// <para>
/// The service is scoped to the browser application, so toasts survive page
/// navigation and are shared by every page and dialog in the shell. The host
/// component subscribes to <see cref="Changed"/> and renders the
/// <see cref="Snapshot"/>.
/// </para>
/// <para>
/// Auto-dismiss uses <see cref="TimeProvider"/> rather than
/// <c>Task.Delay(TimeSpan)</c> so tests can advance time deterministically.
/// Pausing removes the timer and stores the remaining duration; resuming
/// schedules a new timer with that remainder. A toast whose timer is already
/// due when it is paused is removed immediately, so it cannot be stranded
/// without a timer.
/// </para>
/// <para>
/// Every mutation happens under <see cref="_sync"/>, and <see cref="Changed"/>
/// is raised after the lock is released. That keeps a host re-render from
/// deadlocking against a timer callback.
/// </para>
/// </remarks>
internal sealed class QuasarToastService(TimeProvider time, QuasarToastHostOptions options) : IQuasarToastService, IDisposable
{
    /// <summary>
    /// Remaining timer state for one toast. The token source is canceled when
    /// the toast is removed, paused, or cleared.
    /// </summary>
    private sealed class TimerState(CancellationTokenSource cancellation, TimeSpan duration, DateTimeOffset startedAt)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TimeSpan Duration { get; } = duration;
        public DateTimeOffset StartedAt { get; } = startedAt;
    }

    internal const int MaxMessageLength = 1_000;
    internal static readonly TimeSpan MaxTimerDuration = TimeSpan.FromHours(24);
    private readonly Lock _sync = new();
    private readonly List<QuasarToast> _toasts = [];
    private readonly Dictionary<Guid, TimerState> _timers = [];

    /// <summary>
    /// Remaining time for paused toasts. An entry exists after a pause and is
    /// removed when the toast resumes or closes. A toast paused after its
    /// duration elapsed is removed immediately; it does not store a zero.
    /// </summary>
    private readonly Dictionary<Guid, TimeSpan> _remaining = [];
    private bool _disposed;

    public event Action? Changed;

    /// <inheritdoc />
    public Guid Add(string message, QuasarSeverity severity = QuasarSeverity.Normal,
        Action<QuasarToastOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (message.Length > MaxMessageLength)
            message = message[..(MaxMessageLength - 1)] + "…";

        // The callback runs before the toast is tracked, so the host never sees
        // a half-configured toast.
        var toastOptions = new QuasarToastOptions();
        configure?.Invoke(toastOptions);
        // Copy the mutable options after the callback. A caller that captures
        // the callback argument cannot change an open toast later.
        var toastSnapshot = CopyOptions(toastOptions);
        if (toastSnapshot.Duration > MaxTimerDuration)
            toastSnapshot.Duration = MaxTimerDuration;
        var id = Guid.CreateVersion7();
        var toast = new QuasarToast(id, message, severity, toastSnapshot, time.GetUtcNow());

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _toasts.Add(toast);

            // Keep the public list bounded even when a burst of callers adds
            // toasts faster than they can be read.
            while (_toasts.Count > Math.Max(1, options.MaxVisibleToasts))
                RemoveLocked(_toasts[0].Id);
            ScheduleLocked(toast, toastSnapshot.Duration);
        }

        RaiseChanged();
        return id;
    }

    /// <inheritdoc />
    public bool Remove(Guid id)
    {
        bool removed;
        lock (_sync)
            removed = RemoveLocked(id);

        if (removed)
            RaiseChanged();
        return removed;
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_sync)
        {
            foreach (var timer in _timers.Values)
                Cancel(timer);
            _timers.Clear();
            _remaining.Clear();
            _toasts.Clear();
        }

        RaiseChanged();
    }

    /// <inheritdoc />
    public IReadOnlyList<QuasarToast> Snapshot
    {
        get
        {
            lock (_sync)
                return _toasts.ToArray();
        }
    }

    /// <summary>
    /// Stops the auto-dismiss timer and remembers the remaining time. The host
    /// calls this while the pointer or keyboard focus is inside a toast.
    /// </summary>
    internal void Pause(Guid id)
    {
        bool removed;
        lock (_sync)
        {
            if (!_timers.Remove(id, out var timer))
                return;

            Cancel(timer);
            var elapsed = time.GetUtcNow() - timer.StartedAt;
            var remaining = timer.Duration - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                _remaining[id] = remaining;
                removed = false;
            }
            else
            {
                // The timer was already due but its continuation had not run.
                // Remove now instead of leaving a toast with no timer.
                removed = RemoveLocked(id);
            }
        }

        if (removed)
            RaiseChanged();
    }

    /// <summary>
    /// Restarts the auto-dismiss timer with the time left when the toast was
    /// paused. Does nothing when the toast had no remaining time or has already
    /// closed.
    /// </summary>
    internal void Resume(Guid id)
    {
        lock (_sync)
        {
            if (_timers.ContainsKey(id))
                return;
            if (!_remaining.Remove(id, out var remaining) || remaining <= TimeSpan.Zero)
                return;

            var toast = _toasts.FirstOrDefault(candidate => candidate.Id == id);
            if (toast is null)
                return;

            ScheduleLocked(toast, remaining);
        }
    }

    /// <summary>
    /// Starts a dismiss timer unless the toast requires interaction, has no
    /// positive duration, or the service is shutting down. Callers must hold
    /// <see cref="_sync"/>.
    /// </summary>
    private void ScheduleLocked(QuasarToast toast, TimeSpan duration)
    {
        if (toast.Options.RequireInteraction || duration <= TimeSpan.Zero || _disposed)
            return;

        // A resume should not create a second timer while one is still active.
        if (_timers.Remove(toast.Id, out var existing))
            Cancel(existing);

        var cancellation = new CancellationTokenSource();
        _timers[toast.Id] = new TimerState(cancellation, duration, time.GetUtcNow());
        _ = RemoveAfterAsync(toast.Id, duration, cancellation);
    }

    /// <summary>
    /// Waits for the toast duration and removes the toast. Cancellation is an
    /// expected part of pause, remove, clear, and dispose, so it is swallowed.
    /// </summary>
    private async Task RemoveAfterAsync(Guid id, TimeSpan duration, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(duration, time, cancellation.Token);
            Remove(id);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            // This task owns the source; pause/remove/clear only cancel it.
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Removes a toast and its timer state. Callers must hold
    /// <see cref="_sync"/>; they are responsible for raising
    /// <see cref="Changed"/>.
    /// </summary>
    private bool RemoveLocked(Guid id)
    {
        var index = _toasts.FindIndex(candidate => candidate.Id == id);
        if (index < 0)
            return false;

        _toasts.RemoveAt(index);
        if (_timers.Remove(id, out var timer))
            Cancel(timer);
        _remaining.Remove(id);
        return true;
    }

    /// <summary>
    /// Cancels a timer without racing the task that disposes its source.
    /// </summary>
    private static void Cancel(TimerState timer)
    {
        try
        {
            timer.Cancellation.Cancel();
        }
        catch (ObjectDisposedException error)
        {
            // The timer task owns the source and may already have disposed it.
            System.Diagnostics.Debug.WriteLine(error);
        }
    }

    /// <summary>
    /// Copies the mutable options supplied to the configure callback, so the
    /// tracked toast is independent of the caller's object.
    /// </summary>
    private static QuasarToastOptions CopyOptions(QuasarToastOptions source) => new()
    {
        Duration = source.Duration,
        RequireInteraction = source.RequireInteraction,
        Closable = source.Closable,
        ShowIcon = source.ShowIcon,
        Icon = source.Icon,
        ActionText = source.ActionText,
        OnAction = source.OnAction,
        Class = source.Class,
        Style = source.Style,
    };

    /// <summary>
    /// Stops every timer and clears the toast list. Pending callers of
    /// <see cref="RemoveAfterAsync"/> observe cancellation and exit quietly.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var timer in _timers.Values)
                Cancel(timer);
            _timers.Clear();
            _remaining.Clear();
            _toasts.Clear();
        }

        Changed = null;
    }

    private void RaiseChanged() => Changed?.Invoke();
}
