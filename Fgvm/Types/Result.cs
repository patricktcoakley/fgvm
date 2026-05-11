namespace Fgvm.Types;

/// <summary>
///     Represents a unit type for operations that don't return a value.
/// </summary>
public readonly record struct Unit
{
    public static readonly Unit Value = new();
}

/// <summary>
///     Represents the result of an operation that can either succeed or fail.
/// </summary>
/// <typeparam name="T">The type of the success value</typeparam>
/// <typeparam name="E">The type of the error value</typeparam>
public abstract record Result<T, E>
{
    public sealed record Success(T Value) : Result<T, E>;

    public sealed record Failure(E Error) : Result<T, E>;

    public static implicit operator Result<T, E>(T value) => new Success(value);
}
