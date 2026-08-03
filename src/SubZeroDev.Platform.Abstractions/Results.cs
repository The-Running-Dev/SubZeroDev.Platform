namespace SubZeroDev.Platform.Abstractions;

/// <summary>The base of every error that crosses a module boundary. No bare exceptions and no
/// string errors cross one.</summary>
/// <param name="Code">A stable, enumerable code — never a message string.</param>
public abstract record PlatformError(string Code)
{
    /// <summary>Whether a caller may retry the operation. Platform itself retries nothing on the
    /// request path.</summary>
    public abstract bool IsRetryable { get; }
}

/// <summary>The outcome of an operation that produces a value.</summary>
/// <typeparam name="T">The value produced on success.</typeparam>
/// <typeparam name="TError">The error produced on failure.</typeparam>
public readonly struct Result<T, TError>
    where TError : PlatformError
{
    private readonly T _value;
    private readonly TError? _error;

    private Result(bool isSuccess, T value, TError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>The value produced. Reading this on a failure is a defect in the caller and throws.</summary>
    /// <exception cref="PlatformContractViolationException">The result is a failure.</exception>
    public T Value => IsSuccess
        ? _value
        : throw new PlatformContractViolationException(ContractViolation.ResultAccessedIncorrectly());

    /// <summary>The error produced. Reading this on a success is a defect in the caller and throws.</summary>
    /// <exception cref="PlatformContractViolationException">The result is a success.</exception>
    public TError Error => IsSuccess
        ? throw new PlatformContractViolationException(ContractViolation.ResultAccessedIncorrectly())
        : _error!;

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The value produced.</param>
    /// <returns>A successful result.</returns>
    public static Result<T, TError> Success(T value) => new(true, value, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The error produced.</param>
    /// <returns>A failed result.</returns>
    public static Result<T, TError> Failure(TError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T, TError>(false, default!, error);
    }

    /// <summary>Projects both outcomes onto one type, so neither can be read without being handled.</summary>
    /// <typeparam name="TOut">The projected type.</typeparam>
    /// <param name="onSuccess">Applied to the value when the result succeeded.</param>
    /// <param name="onFailure">Applied to the error when the result failed.</param>
    /// <returns>The projection of whichever outcome occurred.</returns>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<TError, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return IsSuccess ? onSuccess(_value) : onFailure(_error!);
    }
}

/// <summary>The outcome of an operation that produces no value.</summary>
/// <typeparam name="TError">The error produced on failure.</typeparam>
public readonly struct Result<TError>
    where TError : PlatformError
{
    private readonly TError? _error;

    private Result(bool isSuccess, TError? error)
    {
        IsSuccess = isSuccess;
        _error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>The error produced. Reading this on a success is a defect in the caller and throws.</summary>
    /// <exception cref="PlatformContractViolationException">The result is a success.</exception>
    public TError Error => IsSuccess
        ? throw new PlatformContractViolationException(ContractViolation.ResultAccessedIncorrectly())
        : _error!;

    /// <summary>Creates a successful result.</summary>
    /// <returns>A successful result.</returns>
    public static Result<TError> Success() => new(true, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The error produced.</param>
    /// <returns>A failed result.</returns>
    public static Result<TError> Failure(TError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<TError>(false, error);
    }
}

/// <summary>A defect in the caller rather than a runtime condition — a result read the wrong way,
/// an ambient accessor reached with no scope open, or an enqueue missing what it must be given.</summary>
public sealed class PlatformContractViolationException : Exception
{
    /// <summary>Creates the exception for a contract violation.</summary>
    /// <param name="error">The violation, carried so its code is stable and enumerable.</param>
    public PlatformContractViolationException(PlatformError error)
        : base(error?.Code)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>The violation, carried so the code is stable and enumerable rather than a message string.</summary>
    public PlatformError Error { get; }
}

/// <summary>The violations <see cref="PlatformContractViolationException"/> carries. Never returned.</summary>
/// <param name="Code">The stable code.</param>
public sealed record ContractViolation(string Code) : PlatformError(Code)
{
    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>An ambient accessor, or an enqueue, was reached with no operation scope open.</summary>
    /// <returns>The violation.</returns>
    public static ContractViolation NoAmbientOperationScope() => new(nameof(NoAmbientOperationScope));

    /// <summary><c>Value</c> was read on a failure, or <c>Error</c> on a success.</summary>
    /// <returns>The violation.</returns>
    public static ContractViolation ResultAccessedIncorrectly() => new(nameof(ResultAccessedIncorrectly));
}
