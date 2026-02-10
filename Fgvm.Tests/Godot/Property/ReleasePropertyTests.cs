using Fgvm.Godot;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Fgvm.Tests.Godot.Property;

public class ReleasePropertyTests
{
    private static Gen<int?> PatchGen =>
        Gen.OneOf(
            Gen.Constant((int?)null),
            Gen.Choose(0, 99).Select<int, int?>(x => x));

    private static Gen<ReleaseType> TypeGen =>
        Gen.OneOf(
            Gen.Constant(ReleaseType.Stable()),
            Gen.Choose(1, 99).Select(ReleaseType.Rc),
            Gen.Choose(1, 99).Select(ReleaseType.Beta),
            Gen.Choose(1, 99).Select(ReleaseType.Alpha),
            Gen.Choose(1, 99).Select(ReleaseType.Dev));

    private static Gen<RuntimeEnvironment> RuntimeGen =>
        Gen.Elements(RuntimeEnvironment.Mono, RuntimeEnvironment.Standard);

    private static Gen<(int Major, int Minor)> VersionGen =>
        Gen.Choose(1, 99)
            .SelectMany(_ =>
                Gen.Choose(0, 99), (major, minor) =>
                (Major: major, Minor: minor));

    private static Arbitrary<Release> ReleaseGen =>
        (from version in VersionGen
            from patch in PatchGen
            from type in TypeGen
            from runtime in RuntimeGen
            select new Release(
                version.Major,
                version.Minor,
                patch: patch,
                type: type,
                runtimeEnvironment: runtime)
        )
        .ToArbitrary();

    [Property]
    public FsCheck.Property Release_ToString_TryParse_Roundtrip()
    {
        return Prop.ForAll(ReleaseGen, release =>
        {
            var str = release.ReleaseNameWithRuntime;
            var parsed = Release.TryParse(str);

            return parsed != null &&
                   parsed.Major == release.Major &&
                   parsed.Minor == release.Minor &&
                   parsed.Patch == release.Patch &&
                   parsed.Type == release.Type &&
                   parsed.ReleaseNameWithRuntime == release.ReleaseNameWithRuntime &&
                   parsed.RuntimeEnvironment == release.RuntimeEnvironment;
        });
    }

    [Property]
    public FsCheck.Property Release_OrderingIsTransitive()
    {
        var releaseListGen = ReleaseGen.Generator.ListOf(10)
            .ToArbitrary();


        return Prop.ForAll(
            releaseListGen,
            releases =>
            {
                if (releases[0] >= releases[1] &&
                    releases[1] >= releases[2])
                {
                    return releases[0] >= releases[2];
                }

                return true;
            });
    }

    [Property]
    public FsCheck.Property Release_LessThanOrEqual_Consistent()
    {
        return Prop.ForAll(ReleaseGen, ReleaseGen, (x, y) =>
        {
            if (x <= y && y <= x)
            {
                return x.Equals(y);
            }

            return true;
        });
    }


    [Property]
    public FsCheck.Property Release_NullComparison()
    {
        return Prop.ForAll(ReleaseGen, release =>
            release.CompareTo(null) > 0);
    }
}
