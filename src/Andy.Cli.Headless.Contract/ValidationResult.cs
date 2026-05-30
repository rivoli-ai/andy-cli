namespace Andy.Cli.Headless.Contract;

/// <summary>
/// Outcome of validating a JSON document against a headless contract schema.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationResult"/> class.
    /// </summary>
    /// <param name="isValid">Whether the document satisfied the schema.</param>
    /// <param name="errors">Human-readable validation error messages. Empty when valid.</param>
    public ValidationResult(bool isValid, IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Errors = errors ?? Array.Empty<string>();
    }

    /// <summary>
    /// Gets a value indicating whether the document satisfied the schema.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// Gets the validation error messages. Empty when <see cref="IsValid"/> is <c>true</c>.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// Creates a successful (valid) result with no errors.
    /// </summary>
    /// <returns>A valid <see cref="ValidationResult"/>.</returns>
    public static ValidationResult Success() => new(true, Array.Empty<string>());

    /// <summary>
    /// Creates a failed (invalid) result carrying the supplied error messages.
    /// </summary>
    /// <param name="errors">One or more validation error messages.</param>
    /// <returns>An invalid <see cref="ValidationResult"/>.</returns>
    public static ValidationResult Failure(IReadOnlyList<string> errors) => new(false, errors);
}
