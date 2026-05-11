namespace Fgvm.Godot;

public sealed record ReleaseType : IComparable<ReleaseType>
{
    // In order
    public static readonly string[] Prefixes = ["stable", "rc", "beta", "alpha", "dev"];

    private ReleaseType(string value, int? version = null)
    {
        Value = value;
        Version = version;
    }

    public string Value { get; }
    public int? Version { get; }

    public int CompareTo(ReleaseType? other)
    {
        if (ReferenceEquals(this, other))
        {
            return 0;
        }

        if (other is null)
        {
            return 1;
        }

        return (Value, other.Value) switch
        {
            ("stable", "stable") => 0,
            ("stable", _) => 1, // Stable is "greater" for preference ordering
            (_, "stable") => -1, // Other types are "less" than stable
            ("rc", "rc") => Nullable.Compare(Version, other.Version), // Higher RC numbers are greater
            ("rc", _) => 1, // RC is greater than beta/alpha/dev
            (_, "rc") => -1, // beta/alpha/dev are less than RC
            ("beta", "beta") => Nullable.Compare(Version, other.Version), // Higher beta numbers are greater
            ("beta", _) => 1, // Beta is greater than alpha/dev
            (_, "beta") => -1, // alpha/dev are less than beta
            ("alpha", "alpha") => Nullable.Compare(Version, other.Version), // Higher alpha numbers are greater
            ("alpha", _) => 1, // Alpha is greater than dev
            (_, "alpha") => -1, // dev is less than alpha
            ("dev", "dev") => Nullable.Compare(Version, other.Version), // Higher dev numbers are greater
            ("dev", _) => -1, // dev is less than everything else
            (_, "dev") => 1, // everything else is greater than dev
            _ => 0 // Should never happen due to private constructor and factory methods
        };
    }

    public static ReleaseType Stable() => new("stable");
    public static ReleaseType Rc(int version) => new("rc", version);
    public static ReleaseType Beta(int version) => new("beta", version);
    public static ReleaseType Alpha(int version) => new("alpha", version);
    public static ReleaseType Dev(int version) => new("dev", version);

    internal static ReleaseType? TryParse(string input)
    {
        var split = input.Split('-');

        var possiblePrefix = split.FirstOrDefault(x => Prefixes
            .Any(p => x.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

        if (possiblePrefix == null)
        {
            return null;
        }

        if (possiblePrefix.Equals("stable", StringComparison.CurrentCultureIgnoreCase))
        {
            return Stable();
        }

        // Find the first possible version number
        var possibleVersion = possiblePrefix[possiblePrefix.TakeWhile(char.IsLetter).Count()..];

        // Reassign up to possible version
        possiblePrefix = possiblePrefix[..^possibleVersion.Length].ToLower();

        if (string.IsNullOrEmpty(possibleVersion) || !int.TryParse(possibleVersion, out var version) || version < 1)
        {
            return null;
        }

        return possiblePrefix switch
        {
            "rc" => Rc(version),
            "beta" => Beta(version),
            "alpha" => Alpha(version),
            "dev" => Dev(version),
            _ => null
        };
    }

    public override string ToString() => Version.HasValue ? $"{Value}{Version}" : Value;

    public static implicit operator string(ReleaseType type) => type.ToString();
}
