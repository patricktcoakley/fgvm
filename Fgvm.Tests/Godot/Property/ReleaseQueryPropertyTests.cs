using Fgvm.Godot;
using Fgvm.Tests.Godot.ReleaseManager;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Fgvm.Tests.Godot.Property;

public class ReleaseQueryPropertyTests
{
    private static Gen<string> VersionGen =>
        Gen.OneOf(
            Gen.Elements("4.5", "4.4", "4.3", "4.2", "4.1", "4.0", "3.6", "3.5"),
            Gen.Choose(1, 10).Zip(Gen.Choose(0, 9)).Select(t => $"{t.Item1}.{t.Item2}")
        );

    private static Gen<string> ReleaseTypeStringGen =>
        Gen.OneOf(
            Gen.Constant("stable"),
            Gen.Choose(1, 10).Select(v => $"rc{v}"),
            Gen.Choose(1, 10).Select(v => $"beta{v}"),
            Gen.Choose(1, 10).Select(v => $"alpha{v}"),
            Gen.Choose(1, 10).Select(v => $"dev{v}")
        );

    private static Gen<string> ReleaseNameGen =>
        VersionGen.Zip(ReleaseTypeStringGen).Select(t => $"{t.Item1}-{t.Item2}");

    private static Gen<string[]> ReleaseNamesGen =>
        ReleaseNameGen.ListOf()
            .Where(list => list.Count > 0)
            .Select(list => list.Distinct().ToArray());

    [Property]
    public FsCheck.Property QueryWithoutReleaseType_SelectsHighestPriorityType()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        return Prop.ForAll(VersionGen.ToArbitrary(), ReleaseNamesGen.ToArbitrary(), (version, allReleases) =>
        {
            // Filter to only releases matching the version
            var matchingReleases = allReleases
                .Where(r => r.StartsWith(version + "-"))
                .ToArray();

            if (matchingReleases.Length == 0)
            {
                return true;
            }

            var result = releaseManager.TryFindReleaseByQuery([version], matchingReleases);

            if (result == null)
            {
                return true;
            }

            // Parse all matching releases to get their types
            var parsedReleases = matchingReleases
                .Select(Release.TryParse)
                .OfType<Release>()
                .ToArray();

            if (parsedReleases.Length == 0)
            {
                return true;
            }

            // Query selection is an explicit stability-first policy, separate from Release.CompareTo.
            var expectedBest = OrderBySelectionPreference(parsedReleases)
                .First();

            return result.ReleaseName == expectedBest.ReleaseName;
        });
    }

    [Property]
    public FsCheck.Property BetaIsPreferredOverDev()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        return Prop.ForAll(VersionGen.ToArbitrary(), Gen.Choose(1, 10).ToArbitrary(), Gen.Choose(1, 10).ToArbitrary(),
            (version, betaNum, devNum) =>
            {
                var releases = new[]
                {
                    $"{version}-beta{betaNum}",
                    $"{version}-dev{devNum}"
                };

                var result = releaseManager.TryFindReleaseByQuery([version], releases);

                return result?.ReleaseName.Contains("beta") == true;
            });
    }

    [Property]
    public FsCheck.Property StableIsPreferredOverAllOthers()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        return Prop.ForAll(VersionGen.ToArbitrary(), ReleaseTypeStringGen.ToArbitrary(), (version, otherType) =>
        {
            if (otherType == "stable")
            {
                return true;
            }

            var releases = new[]
            {
                $"{version}-stable",
                $"{version}-{otherType}"
            };

            var result = releaseManager.TryFindReleaseByQuery([version], releases);

            return result?.ReleaseName.Contains("stable") == true;
        });
    }

    [Property]
    public FsCheck.Property RcIsPreferredOverBetaAlphaDev()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var combinedGen = VersionGen
            .Zip(Gen.Choose(1, 10))
            .Zip(Gen.Elements("beta", "alpha", "dev"))
            .Zip(Gen.Choose(1, 10))
            .Select(t => new { Version = t.Item1.Item1.Item1, RcNum = t.Item1.Item1.Item2, LowerType = t.Item1.Item2, LowerNum = t.Item2 });

        return Prop.ForAll(combinedGen.ToArbitrary(), data =>
        {
            var releases = new[]
            {
                $"{data.Version}-rc{data.RcNum}",
                $"{data.Version}-{data.LowerType}{data.LowerNum}"
            };

            var result = releaseManager.TryFindReleaseByQuery([data.Version], releases);

            return result?.ReleaseName.Contains("rc") == true;
        });
    }

    [Property]
    public FsCheck.Property AlphaIsPreferredOverDev()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        return Prop.ForAll(VersionGen.ToArbitrary(), Gen.Choose(1, 10).ToArbitrary(), Gen.Choose(1, 10).ToArbitrary(),
            (version, alphaNum, devNum) =>
            {
                var releases = new[]
                {
                    $"{version}-alpha{alphaNum}",
                    $"{version}-dev{devNum}"
                };

                var result = releaseManager.TryFindReleaseByQuery([version], releases);

                return result?.ReleaseName.Contains("alpha") == true;
            });
    }

    [Property]
    public FsCheck.Property HigherVersionNumbersPreferredWithinSameType()
    {
        var releaseManager = new ReleaseManagerBuilder().Build();

        var combinedGen = VersionGen
            .Zip(Gen.Elements("rc", "beta", "alpha", "dev"))
            .Zip(Gen.Choose(1, 5))
            .Zip(Gen.Choose(6, 10))
            .Select(t => new
                { Version = t.Item1.Item1.Item1, ReleaseType = t.Item1.Item1.Item2, LowerNum = t.Item1.Item2, HigherNum = t.Item2 });

        return Prop.ForAll(combinedGen.ToArbitrary(), data =>
        {
            var releases = new[]
            {
                $"{data.Version}-{data.ReleaseType}{data.LowerNum}",
                $"{data.Version}-{data.ReleaseType}{data.HigherNum}"
            };

            var result = releaseManager.TryFindReleaseByQuery([data.Version], releases);

            return result?.ReleaseName.Contains($"{data.ReleaseType}{data.HigherNum}") == true;
        });
    }

    private static IOrderedEnumerable<Release> OrderBySelectionPreference(IEnumerable<Release> releases) =>
        releases
            .OrderByDescending(release => release.Major)
            .ThenByDescending(release => release.Type)
            .ThenByDescending(release => release.Minor)
            .ThenByDescending(release => release.Patch)
            .ThenByDescending(release => release.RuntimeEnvironment)
            .ThenByDescending(release => release.ReleaseNameWithRuntime, StringComparer.Ordinal);
}
