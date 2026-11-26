namespace Quasar.Ui.Components;

/// <summary>
/// The result of one field validation run: zero or more messages.
/// </summary>
/// <remarks>
/// A single message converts implicitly from <see cref="string"/>; use
/// <see cref="FromErrors"/> when a rule produces several messages at once.
/// </remarks>
public readonly struct QuasarValidationResult
{
    private readonly IReadOnlyList<string>? _errors;

    private QuasarValidationResult(IReadOnlyList<string> errors) => _errors = errors;

    /// <summary>A valid result with no messages.</summary>
    public static QuasarValidationResult Valid => default;

    /// <summary>The validation messages. Empty when the value is valid.</summary>
    public IReadOnlyList<string> Errors => _errors ?? Array.Empty<string>();

    /// <summary>True when the result carries no messages.</summary>
    public bool IsValid => _errors is null || _errors.Count == 0;

    /// <summary>
    /// Creates a result from the supplied messages, ignoring null, empty and
    /// whitespace entries.
    /// </summary>
    public static QuasarValidationResult FromErrors(params string[] errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        List<string>? filtered = null;
        foreach (var error in errors)
        {
            if (string.IsNullOrWhiteSpace(error))
                continue;

            filtered ??= new List<string>(errors.Length);
            filtered.Add(error);
        }

        return filtered is null ? Valid : new QuasarValidationResult(filtered);
    }

    /// <summary>Converts one message, or <see langword="null"/>, into a result.</summary>
    public static implicit operator QuasarValidationResult(string? error) =>
        string.IsNullOrWhiteSpace(error) ? Valid : FromErrors(error);
}
