namespace Fgvm.Types;

public abstract record QueryError
{
    public sealed record EmptyQuery : QueryError;

    public sealed record InvalidQuery(string Message) : QueryError;

    public sealed record NotFound(string Query) : QueryError;
}

public abstract record CompatibilityError
{
    public sealed record NoInstalledVersions : CompatibilityError;

    public sealed record NotFound(string ProjectVersion, bool IsDotNet) : CompatibilityError;
}
