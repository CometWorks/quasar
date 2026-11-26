using System.Diagnostics;
using Microsoft.AspNetCore.Components;

namespace Quasar.Ui.Components;

/// <summary>
/// Base class for native Quasar form controls. Owns value binding, required and
/// asynchronous validation, dirty tracking, and the label, hint and message
/// accessibility wiring.
/// </summary>
/// <typeparam name="T">The bound value type.</typeparam>
/// <remarks>
/// <para>
/// A control commits its value when the user finishes an edit, for example a
/// change event or a selection. Text-like controls with <c>Immediate</c> set
/// commit after a short pause in typing instead, and on blur. Committing raises
/// <see cref="ValueChanged"/> and runs validation.
/// </para>
/// <para>
/// Validation is asynchronous by design. <see cref="Required"/> runs first and
/// short-circuits the delegate for an empty value. The delegate can return one
/// message or several; every message is rendered and announced. A newer run
/// cancels the previous one, and a generation check drops stale completions that
/// arrive after cancellation. A field with neither <see cref="Required"/> nor
/// <see cref="Validation"/> never validates.
/// </para>
/// </remarks>
public abstract class QuasarFieldBase<T> : ComponentBase, IQuasarField, IDisposable
{
    private const int DebounceMilliseconds = 300;
    private const string RequiredMessage = "This field is required.";

    private readonly string _generatedId = $"quasar-input-{Guid.NewGuid():n}";

    private CancellationTokenSource? _commitTimer;
    private CancellationTokenSource? _validation;
    private IReadOnlyList<string> _errors = Array.Empty<string>();
    private T _baseline = default!;
    private int _generation;
    private bool _baselineCaptured;
    private bool _pending;
    private bool _validatedCurrent;
    private bool _disposed;

    /// <summary>The form that owns this field, when the field is inside one.</summary>
    [CascadingParameter] public QuasarForm? Form { get; set; }

    /// <summary>The bound value.</summary>
    [Parameter] public T Value { get; set; } = default!;

    /// <summary>Raised when the user commits a new value.</summary>
    [Parameter] public EventCallback<T> ValueChanged { get; set; }

    /// <summary>Visible label, also used as the accessible name.</summary>
    [Parameter] public string? Label { get; set; }

    /// <summary>Marks the value as required and renders the required marker.</summary>
    [Parameter] public bool Required { get; set; }

    /// <summary>
    /// Asynchronous validation rule. It runs after <see cref="Required"/> and
    /// receives a token that is cancelled when the value changes or a newer
    /// validation starts.
    /// </summary>
    [Parameter] public QuasarValidator<T>? Validation { get; set; }

    /// <summary>Disables the control.</summary>
    [Parameter] public bool Disabled { get; set; }

    /// <summary>
    /// Makes the control read-only. Native selects, checkboxes and radio buttons
    /// have no read-only state, so those controls render as disabled.
    /// </summary>
    [Parameter] public bool ReadOnly { get; set; }

    /// <summary>Helper text shown below the control.</summary>
    [Parameter] public string? Hint { get; set; }

    /// <summary>Placeholder text for the native input.</summary>
    [Parameter] public string? Placeholder { get; set; }

    /// <summary>Explicit element id. Generated when omitted.</summary>
    [Parameter] public string? Id { get; set; }

    /// <summary>Additional class applied to the field wrapper.</summary>
    [Parameter] public string? Class { get; set; }

    /// <summary>
    /// Additional attributes forwarded to the native input, such as
    /// <c language="html">autocomplete</c> or <c language="html">data-*</c>.
    /// Values are trusted markup from the caller.
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>The current value, including an edit that is not committed yet.</summary>
    protected T CurrentValue { get; private set; } = default!;

    /// <summary>True while an edit is waiting for the debounce to commit.</summary>
    protected bool HasPendingEdit => _pending;

    /// <summary>Current validation messages.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>The first validation message, if any.</summary>
    public string? Error => _errors.Count == 0 ? null : _errors[0];

    /// <inheritdoc />
    public bool IsValid => !IsValidating && _errors.Count == 0;

    /// <inheritdoc />
    public bool IsValidating { get; private set; }

    /// <inheritdoc />
    public bool IsDirty =>
        _baselineCaptured && !EqualityComparer<T>.Default.Equals(CurrentValue, _baseline);

    /// <summary>Element id used by the label and the native input.</summary>
    protected string FieldId => string.IsNullOrWhiteSpace(Id) ? _generatedId : Id;

    /// <summary>Element id of the hint.</summary>
    protected string HintId => $"{FieldId}-hint";

    /// <summary>Element id of the message container.</summary>
    protected string MessagesId => $"{FieldId}-messages";

    /// <summary>Space-separated ids referenced by <c language="html">aria-describedby</c>.</summary>
    protected string? DescribedBy
    {
        get
        {
            var hint = string.IsNullOrWhiteSpace(Hint) ? null : HintId;
            var messages = _errors.Count == 0 ? null : MessagesId;
            return (hint, messages) switch
            {
                (null, null) => null,
                (not null, null) => hint,
                (null, not null) => messages,
                _ => $"{hint} {messages}",
            };
        }
    }

    /// <summary>Class list applied to the field wrapper.</summary>
    protected string FieldClass
    {
        get
        {
            var classes = "quasar-input";
            if (_errors.Count > 0)
                classes += " is-invalid";
            if (IsValidating)
                classes += " is-validating";
            if (!string.IsNullOrWhiteSpace(Class))
                classes += " " + Class;
            return classes;
        }
    }

    /// <inheritdoc />
    protected override void OnInitialized() => Form?.RegisterField(this);

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        // An uncommitted edit is the newer truth; a parent re-render must not
        // overwrite what the user is still typing.
        if (!_pending)
            CurrentValue = Value;
    }

    /// <summary>
    /// Commits a value now: raises <see cref="ValueChanged"/> and validates.
    /// </summary>
    protected async Task SetValueAsync(T value)
    {
        CancelCommitTimer();
        CurrentValue = value;
        _validatedCurrent = false;
        await CommitAsync();
    }

    /// <summary>
    /// Records an edit without committing it. The value is committed after a
    /// pause in typing, or when the control loses focus.
    /// </summary>
    protected void EditValue(T value)
    {
        CurrentValue = value;
        _pending = true;
        _validatedCurrent = false;
        Form?.NotifyFieldChanged();
        StartCommitTimer();
    }

    /// <summary>
    /// Commits any pending edit, or validates once when the value was never
    /// validated, when the native input loses focus.
    /// </summary>
    protected async Task OnBlurAsync()
    {
        if (_pending)
        {
            CancelCommitTimer();
            await CommitAsync();
        }
        else if (!_validatedCurrent)
        {
            await RunValidationAsync();
        }
    }

    /// <inheritdoc />
    public async Task ValidateAsync()
    {
        if (_pending)
        {
            CancelCommitTimer();
            await CommitAsync();
            return;
        }

        await RunValidationAsync();
    }

    /// <inheritdoc />
    public void CaptureBaseline()
    {
        _baseline = CurrentValue;
        _baselineCaptured = true;
    }

    /// <summary>
    /// Returns true when <paramref name="value"/> counts as empty for
    /// <see cref="Required"/>. Controls override this for their own value type.
    /// </summary>
    protected virtual bool IsEmpty(T value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        System.Collections.ICollection collection => collection.Count == 0,
        _ => false,
    };

    private async Task CommitAsync()
    {
        _pending = false;
        await ValueChanged.InvokeAsync(CurrentValue);
        Form?.NotifyFieldChanged();
        await RunValidationAsync();
    }

    private void StartCommitTimer()
    {
        CancelCommitTimer();
        var cts = new CancellationTokenSource();
        _commitTimer = cts;
        _ = CommitAfterPauseAsync(cts);
    }

    private async Task CommitAfterPauseAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed || !ReferenceEquals(_commitTimer, cts))
            return;

        _commitTimer = null;
        cts.Dispose();

        try
        {
            await CommitAsync();
        }
        catch (Exception ex)
        {
            await DispatchExceptionAsync(ex);
        }
    }

    private void CancelCommitTimer()
    {
        var cts = _commitTimer;
        if (cts is null)
            return;

        _commitTimer = null;
        cts.Cancel();
        cts.Dispose();
    }

    private async Task RunValidationAsync()
    {
        if (_disposed)
            return;

        _validatedCurrent = true;
        var generation = ++_generation;
        CancelValidation();

        if (!Required && Validation is null)
        {
            if (_errors.Count > 0 || IsValidating)
            {
                _errors = Array.Empty<string>();
                IsValidating = false;
                Form?.NotifyFieldChanged();
                StateHasChanged();
            }

            return;
        }

        var cts = new CancellationTokenSource();
        _validation = cts;
        IsValidating = true;
        Form?.NotifyFieldChanged();
        StateHasChanged();

        QuasarValidationResult result;
        try
        {
            result = await ComputeValidationAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
            {
                IsValidating = false;
                Form?.NotifyFieldChanged();
                StateHasChanged();
            }

            return;
        }
        catch (Exception ex)
        {
            Trace.TraceError("Quasar field validation failed: {0}", ex);
            result = "Validation failed. Try again.";
        }

        if (generation != _generation || cts.IsCancellationRequested)
            return;

        if (ReferenceEquals(_validation, cts))
        {
            _validation = null;
            cts.Dispose();
        }

        _errors = result.Errors;
        IsValidating = false;

        Form?.NotifyFieldChanged();
        StateHasChanged();
    }

    private void CancelValidation()
    {
        var cts = _validation;
        if (cts is null)
            return;

        _validation = null;
        cts.Cancel();
        cts.Dispose();
    }

    private async Task<QuasarValidationResult> ComputeValidationAsync(
        CancellationToken cancellationToken)
    {
        if (Required && IsEmpty(CurrentValue))
            return RequiredMessage;

        return Validation is null
            ? QuasarValidationResult.Valid
            : await Validation(CurrentValue, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the field's cancellation sources.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;
        if (!disposing)
            return;

        CancelCommitTimer();
        CancelValidation();
        Form?.UnregisterField(this);
    }
}
