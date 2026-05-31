using System.IO.Compression;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Fgvm.Tests.Services;

public class InstallationServiceTests
{
    [Fact]
    public async Task FetchReleaseNames_UsesCatalogCacheByDefault()
    {
        var releaseManager = CreateReleaseManagerMock("4.4-stable");
        var releaseCatalog = new Mock<IReleaseCatalog>();
        releaseCatalog.Setup(x => x.ReadReleaseIds(ReleaseFetchMode.UseCache, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<string[], NetworkError>.Success(["4.4-stable"]));

        var service = CreateService(releaseManager.Object, releaseCatalog.Object);

        var releaseNamesResult = await service.FetchReleaseNames(CancellationToken.None);

        var releaseNames = Assert.IsType<Result<string[], NetworkError>.Success>(releaseNamesResult).Value;
        Assert.Equal(["4.4-stable"], releaseNames);
        releaseCatalog.Verify(x => x.ReadReleaseIds(ReleaseFetchMode.UseCache, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FetchReleaseNames_ForceRemote_ForcesRemoteCatalogRefresh()
    {
        var releaseManager = CreateReleaseManagerMock("4.4-stable");
        var releaseCatalog = new Mock<IReleaseCatalog>();
        releaseCatalog.Setup(x => x.ReadReleaseIds(ReleaseFetchMode.ForceRemote, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<string[], NetworkError>.Success(["4.4-stable"]));

        var service = CreateService(releaseManager.Object, releaseCatalog.Object);

        var releaseNamesResult = await service.FetchReleaseNames(CancellationToken.None, ReleaseFetchMode.ForceRemote);

        var releaseNames = Assert.IsType<Result<string[], NetworkError>.Success>(releaseNamesResult).Value;
        Assert.Equal(["4.4-stable"], releaseNames);
        releaseCatalog.Verify(x => x.ReadReleaseIds(ReleaseFetchMode.ForceRemote, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FetchReleaseNames_SortsByVersionBeforeReleaseType()
    {
        var releaseIds = new[] { "4.5-dev1", "4.4-stable", "4.5.1-rc1", "4.5-stable" };
        var releaseManager = CreateReleaseManagerMock(releaseIds);
        var releaseCatalog = new Mock<IReleaseCatalog>();
        releaseCatalog.Setup(x => x.ReadReleaseIds(ReleaseFetchMode.UseCache, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<string[], NetworkError>.Success(releaseIds));

        var service = CreateService(releaseManager.Object, releaseCatalog.Object);

        var releaseNamesResult = await service.FetchReleaseNames(CancellationToken.None);

        var releaseNames = Assert.IsType<Result<string[], NetworkError>.Success>(releaseNamesResult).Value;
        Assert.Equal(["4.5.1-rc1", "4.5-stable", "4.5-dev1", "4.4-stable"], releaseNames);
    }

    [Fact]
    public async Task FetchReleaseNames_ManifestRefreshFailure_PropagatesSortedCachedReleaseNames()
    {
        var releaseIds = new[] { "4.5-dev1", "4.4-stable", "4.5.1-rc1", "4.5-stable" };
        var releaseManager = CreateReleaseManagerMock(releaseIds);
        var releaseCatalog = new Mock<IReleaseCatalog>();
        releaseCatalog.Setup(x => x.ReadReleaseIds(ReleaseFetchMode.UseCache, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<string[], NetworkError>.Failure(new NetworkError.ManifestRefreshFailure(releaseIds)));

        var service = CreateService(releaseManager.Object, releaseCatalog.Object);

        var releaseNamesResult = await service.FetchReleaseNames(CancellationToken.None);

        var failure = Assert.IsType<Result<string[], NetworkError>.Failure>(releaseNamesResult);
        var manifestRefreshFailure = Assert.IsType<NetworkError.ManifestRefreshFailure>(failure.Error);
        Assert.Equal(["4.5.1-rc1", "4.5-stable", "4.5-dev1", "4.4-stable"], manifestRefreshFailure.releaseIds);
    }

    [Fact]
    public async Task InstallByQueryAsync_InvalidQuery_ReturnsInvalidQueryWithoutRemoteRefresh()
    {
        var query = new[] { "bad-query" };
        var releaseManager = CreateReleaseManagerMock("4.4-stable");
        var releaseCatalog = new Mock<IReleaseCatalog>();
        releaseCatalog.Setup(x => x.ReadReleaseIds(ReleaseFetchMode.UseCache, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<string[], NetworkError>.Success(["4.4-stable"]));

        releaseManager.Setup(x => x.ResolveReleaseQuery(query, It.IsAny<string[]>()))
            .Returns(new Result<Release, QueryError>.Failure(new QueryError.InvalidQuery("Invalid query.")));

        var service = CreateService(releaseManager.Object, releaseCatalog.Object);

        var result = await service.InstallByQueryAsync(
            query,
            new Progress<OperationProgress<InstallationStage>>(),
            cancellationToken: CancellationToken.None);

        var failure = Assert.IsType<Result<InstallationOutcome, InstallationError>.Failure>(result);
        var invalidQuery = Assert.IsType<InstallationError.InvalidQuery>(failure.Error);
        Assert.Equal("Invalid query.", invalidQuery.Message);
        releaseCatalog.Verify(x => x.ReadReleaseIds(ReleaseFetchMode.ForceRemote, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallReleaseAsync_MissingCatalogArtifact_ContinuesWithUnavailableChecksum()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-install-service-tests", Guid.NewGuid().ToString("N"));
        if (Release.TryParse("3.2.1-stable-standard") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        release = release with { OS = OS.MacOS, PlatformString = "osx.64" };

        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.GetZipFile(It.IsAny<string>(), release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<ZipDownload, NetworkError>.Success(
                new ZipDownload(new MemoryStream(CreateZipArchive()))));

        var releaseCatalog = new Mock<IReleaseCatalog>();
        releaseCatalog.Setup(x => x.FindOrHydrateArtifact(release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<ReleaseArtifact, NetworkError>.Success(new ReleaseArtifact(release.ZipFileName, null)));

        var hostSystem = new Mock<IHostSystem>();
        var pathService = new Mock<IPathService>();
        pathService.SetupGet(x => x.RootPath).Returns(rootPath);
        pathService.SetupGet(x => x.ReleasesPath).Returns(Path.Combine(rootPath, "releases.json"));
        pathService.SetupGet(x => x.InstallationsPath).Returns(Path.Combine(rootPath, "installations.json"));
        pathService.SetupGet(x => x.InstallationsDirectoryPath).Returns(Path.Combine(rootPath, "installations"));

        var installationRegistry = new Mock<IInstallationRegistry>();
        installationRegistry.Setup(x => x.FindByReleaseName(release.ReleaseNameWithRuntime))
            .Returns(new Result<Installation, InstallationRegistryError>.Failure(
                new InstallationRegistryError.NotFound(release.ReleaseNameWithRuntime)));

        installationRegistry.Setup(x => x.UpsertInstalled(release, It.IsAny<string>(), It.IsAny<DateTimeOffset?>()))
            .Returns(new Result<Unit, InstallationRegistryError>.Success(Unit.Value));

        var service = new InstallationService(
            hostSystem.Object,
            releaseManager.Object,
            releaseCatalog.Object,
            pathService.Object,
            installationRegistry.Object,
            NullLogger<InstallationService>.Instance);

        try
        {
            var result = await service.InstallReleaseAsync(
                release,
                new Progress<OperationProgress<InstallationStage>>(),
                false,
                CancellationToken.None);

            var success = Assert.IsType<Result<InstallationOutcome, InstallationError>.Success>(result);
            var installation = Assert.IsType<InstallationOutcome.NewInstallation>(success.Value);
            Assert.IsType<ChecksumVerification.Unavailable>(installation.ChecksumStatus);

            var installedFile = Path.Combine(rootPath, InstallationRegistry.CreateRelativeInstallPath(release), "Godot");
            Assert.Equal("fake executable", await File.ReadAllTextAsync(installedFile, CancellationToken.None));
            releaseManager.Verify(x => x.GetZipFile(release.ZipFileName, release, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, true);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ParseTestData))]
    public void ParseSha512SumsContent_ReturnsExpectedSha512SumsContent(string fileName, string hash)
    {
        var artifacts = ReleaseCatalog.ParseSha512SumsContent(ParseSha512SumsContentTestInput());

        var artifact = Assert.Single(artifacts, entry => entry.FileName == fileName);
        Assert.Equal(hash, artifact.Sha512);
    }

    public static IEnumerable<object[]> ParseTestData()
    {
        yield return
        [
            "godot-4.4-dev3.tar.xz",
            "57557f3a05476518f2d9388dba4b2e9a5fd09a6ba7e5247b73919a7d31c846d5b0b2379279543c12cc7cd03ae828cf84751b1a0499d1ad2bb1f546c9372976e3"
        ];

        yield return
        [
            "godot-4.4-dev3.tar.xz.sha256",
            "b6e37f559e52417b6734f18d09f380bc26c0e755457b5d94890dd9515504170d639257876683bab78dde8e260a69e197655717dcbb951f413099fb06f8807e63"
        ];

        yield return
        [
            "godot-lib.4.4.dev3.template_release.aar",
            "28b6d3e96b044a5da92b526c9f2f47814192f64761452757608ac181cd0abd4260e84af53189194b490cd1a197749339c42dfceb51e02aea24feefab79e6a7c8"
        ];

        yield return
        [
            "Godot_v4.4-dev3_android_editor.aab",
            "c9e6f36db7649188011a94f80b4b2f9f485dfff2970fd1b249206a46887999d7390db51a7e9522821370ae7c047f8a66d0b7a92f555fde67ef024b605f5de064"
        ];

        yield return
        [
            "Godot_v4.4-dev3_android_editor.apk",
            "f7e340c6ff04f2f6ef763121cfb889b6899b425b5cb6acdfc4e6570c6d435e6a6e1e5cc48f10422420e6b5fbb3859a706472fd11e904566e7ef6a60ebaf6fd01"
        ];

        yield return
        [
            "Godot_v4.4-dev3_android_editor_horizonos.apk",
            "a768a03a99ae83518aede20d18affca228f42a41bf9e04ffd93f4a7efb8c3c34c6d26f49b22f051b3111cefecb73d773ac2a3d20c7083675bfbd3c1efeb80644"
        ];

        yield return
        [
            "Godot_v4.4-dev3_export_templates.tpz",
            "8ff20ab926b50cf7f6711d15091adabf0239fadf57093f2582d4eb6ac0a0eff89d6a3435fd78ceaace77db65a4ea1d2f6bca8c3184814b5529a84560ccf506c1"
        ];

        yield return
        [
            "Godot_v4.4-dev3_linux.arm32.zip",
            "e1db838a7ece2e741647e7741e2be80c59b3ddfff478158a8ab971b043993375e43378b65e95710e48050805955f39aa013cde06b047bcb5b932fd77972a51a4"
        ];

        yield return
        [
            "Godot_v4.4-dev3_linux.arm64.zip",
            "be0a20badd32fec9206051e0d6fcf899a2e0407e4e95b51ccc1eb93cc5ea00503441c443097d19c441b9cdb5a1d55ba4a214b0a3976a00a0da5460bc9a8138f9"
        ];

        yield return
        [
            "Godot_v4.4-dev3_linux.x86_32.zip",
            "3c0ffd8f604d87f9b24a69f7fb9294b047a7155364d53d79a628cbae4e199ab3d55c2c7169c6a9b112f782f5a9e4ef0c80da3e1e2701e992793827e0b0aa4a0a"
        ];

        yield return
        [
            "Godot_v4.4-dev3_linux.x86_64.zip",
            "91424f503fefbb50374d23cf063bf4944ab0a165aa82a3e15c0b7e9f1447b8ed24e7600896c3d9a58dcb63aa8fc730a7597285e52a40193949af21d4f5cfccd5"
        ];

        yield return
        [
            "Godot_v4.4-dev3_macos.universal.zip",
            "e4db5ca090771882c8ec0c2202e1f516e60f4be487b25564a546e9c66dbbbaa00b2f064310bdededc294fed7cc0aa0a52d1f80ab9fdb3dd86f4d884ed38fb7f5"
        ];

        yield return
        [
            "Godot_v4.4-dev3_web_editor.zip",
            "6d07d4b054808746d1d5ec75ef9e46d567628d3e269b4bf1a2682bb92db36810497c90cf6662486518976285c03821d14f97f7593f8bd8e95bf4073656734971"
        ];

        yield return
        [
            "Godot_v4.4-dev3_win32.exe.zip",
            "300170530f89b4d766a5799c5f267a24aa4ef65577370589613340702e9e8c167e7e123112035d7e2193cb91bee88baf4b32a4575f8a0614c515571cd78a4618"
        ];

        yield return
        [
            "Godot_v4.4-dev3_win64.exe.zip",
            "a6f67a43f734948e62c6da83ff87e42465d7e64d25960a469452e102544b8e82090cef4f5eb4f9eee1ad0525f5cb148fffaa9a62ed6cb5881fc5d69c4cefd317"
        ];

        yield return
        [
            "Godot_v4.4-dev3_windows_arm64.exe.zip",
            "4f07ea2bfad630e6a355e5b8da894998b71581ef8c52053b62e1cf95bee92b6f29e3b23ea665ef8d0624241eb09377ae6f6652039a957280c30c9132b011c56a"
        ];

        yield return
        [
            "godot-lib.4.4.dev3.mono.template_release.aar",
            "ec91f1d9067b8e272c03bfc92928cc743ffe2694904ab0fca9dff9febacdb236a95f0465fda8b3f3eba872d6fe34d5a3cc9752b8bee676a6db188faadadcb4d4"
        ];

        yield return
        [
            "Godot_v4.4-dev3_mono_export_templates.tpz",
            "a29698f60912ec90d2a83a49a91e8fe1a3a4fdf2b6135a54d8844519aaab476dfab9329fa6701d90910b058f4e0c6f10c95237bed7c8b9f6f5311a4e29871817"
        ];

        yield return
        [
            "Godot_v4.4-dev3_mono_linux_arm32.zip",
            "c17d6708c2c737e4ca9710558bdf0aacd0c84c95c502692a76b33c0c0e7a1ed37f9f9326f5d9c84498be589cd5ef0601ef3db9dc26cbefaa8b548227010747bb"
        ];

        yield return
        [
            "Godot_v4.4-dev3_mono_linux_arm64.zip",
            "6a41997b590e0908ffdbf6b26cc56f850fa0848bfa4cbebe3c67372c7c90bc07c65e846d3fdf27466dd6bc5f4dd150d6cc6bf788ca2e04ce490fcbf5aa7bf0f0"
        ];

        yield return
        [
            "Godot_v4.4-dev3_mono_linux_x86_32.zip",
            "1b9f49337ff224e835371abedcc7cca282bccf3d0f93a932ab8687ac3f9f926ad09ded09d41de026b4dbe480b3b4cfbe32a30cc79c225ff1717e7a8ea4bbc64e"
        ];

        yield return
        [
            "Godot_v4.4-dev3_mono_linux_x86_64.zip",
            "2645fff411465c0c28f9862736754668e8a891a09dd961f240bc92d2dc6ae1f865510fdcefebbb17253b7940dc801e305c4d3c93dc1e127dff6550b1ebd8dc25"
        ];

        yield return
        [
            "Godot_v4.4-dev3_mono_macos.universal.zip",
            "c3c17766fdbe70ad68e2ffbdec4aaef3c22ccc5824fe665fdf739505fc651fda7d51660273ff48be14079bc5a787e0676eb56a681947e0ad9779bfbeb1ad42b3"
        ];

        yield return
        [
            "Godot_v4.4-dev3_mono_win32.zip",
            "8898ad9634072bf129885fdd59137c29fffce5fc92fc52c89d9c63b7ac930ce5a9dffc5932cea5787c7e099e52d3a6c177abd5387be7eb19c28409777d0e4cf4"
        ];

        yield return
        [
            "Godot_v4.4-dev3_mono_win64.zip",
            "ae3aaf0f92d4dac271940ee2a4817d004f91231a17429264fd806330e143036d30348fca09f46e0c92cda752819302941a7b48a4358cc12f436d90b13352a5b7"
        ];

        yield return
        [
            "Godot_v4.4-dev3_mono_windows_arm64.zip",
            "461b48ccff128969f8c7105b9a423569325692ad48af0ed838eff216cede765775f176206e21bd9e47bcd0aa0475bab58f3583c183c99db2f5a00cf563dcdea4"
        ];
    }

    private static string ParseSha512SumsContentTestInput() =>
        """
                       57557f3a05476518f2d9388dba4b2e9a5fd09a6ba7e5247b73919a7d31c846d5b0b2379279543c12cc7cd03ae828cf84751b1a0499d1ad2bb1f546c9372976e3  godot-4.4-dev3.tar.xz
                       b6e37f559e52417b6734f18d09f380bc26c0e755457b5d94890dd9515504170d639257876683bab78dde8e260a69e197655717dcbb951f413099fb06f8807e63  godot-4.4-dev3.tar.xz.sha256
                       28b6d3e96b044a5da92b526c9f2f47814192f64761452757608ac181cd0abd4260e84af53189194b490cd1a197749339c42dfceb51e02aea24feefab79e6a7c8  godot-lib.4.4.dev3.template_release.aar
                       c9e6f36db7649188011a94f80b4b2f9f485dfff2970fd1b249206a46887999d7390db51a7e9522821370ae7c047f8a66d0b7a92f555fde67ef024b605f5de064  Godot_v4.4-dev3_android_editor.aab
                       f7e340c6ff04f2f6ef763121cfb889b6899b425b5cb6acdfc4e6570c6d435e6a6e1e5cc48f10422420e6b5fbb3859a706472fd11e904566e7ef6a60ebaf6fd01  Godot_v4.4-dev3_android_editor.apk
                       a768a03a99ae83518aede20d18affca228f42a41bf9e04ffd93f4a7efb8c3c34c6d26f49b22f051b3111cefecb73d773ac2a3d20c7083675bfbd3c1efeb80644  Godot_v4.4-dev3_android_editor_horizonos.apk
                       8ff20ab926b50cf7f6711d15091adabf0239fadf57093f2582d4eb6ac0a0eff89d6a3435fd78ceaace77db65a4ea1d2f6bca8c3184814b5529a84560ccf506c1  Godot_v4.4-dev3_export_templates.tpz
                       e1db838a7ece2e741647e7741e2be80c59b3ddfff478158a8ab971b043993375e43378b65e95710e48050805955f39aa013cde06b047bcb5b932fd77972a51a4  Godot_v4.4-dev3_linux.arm32.zip
                       be0a20badd32fec9206051e0d6fcf899a2e0407e4e95b51ccc1eb93cc5ea00503441c443097d19c441b9cdb5a1d55ba4a214b0a3976a00a0da5460bc9a8138f9  Godot_v4.4-dev3_linux.arm64.zip
                       3c0ffd8f604d87f9b24a69f7fb9294b047a7155364d53d79a628cbae4e199ab3d55c2c7169c6a9b112f782f5a9e4ef0c80da3e1e2701e992793827e0b0aa4a0a  Godot_v4.4-dev3_linux.x86_32.zip
                       91424f503fefbb50374d23cf063bf4944ab0a165aa82a3e15c0b7e9f1447b8ed24e7600896c3d9a58dcb63aa8fc730a7597285e52a40193949af21d4f5cfccd5  Godot_v4.4-dev3_linux.x86_64.zip
                       e4db5ca090771882c8ec0c2202e1f516e60f4be487b25564a546e9c66dbbbaa00b2f064310bdededc294fed7cc0aa0a52d1f80ab9fdb3dd86f4d884ed38fb7f5  Godot_v4.4-dev3_macos.universal.zip
                       6d07d4b054808746d1d5ec75ef9e46d567628d3e269b4bf1a2682bb92db36810497c90cf6662486518976285c03821d14f97f7593f8bd8e95bf4073656734971  Godot_v4.4-dev3_web_editor.zip
                       300170530f89b4d766a5799c5f267a24aa4ef65577370589613340702e9e8c167e7e123112035d7e2193cb91bee88baf4b32a4575f8a0614c515571cd78a4618  Godot_v4.4-dev3_win32.exe.zip
                       a6f67a43f734948e62c6da83ff87e42465d7e64d25960a469452e102544b8e82090cef4f5eb4f9eee1ad0525f5cb148fffaa9a62ed6cb5881fc5d69c4cefd317  Godot_v4.4-dev3_win64.exe.zip
                       4f07ea2bfad630e6a355e5b8da894998b71581ef8c52053b62e1cf95bee92b6f29e3b23ea665ef8d0624241eb09377ae6f6652039a957280c30c9132b011c56a  Godot_v4.4-dev3_windows_arm64.exe.zip
                       ec91f1d9067b8e272c03bfc92928cc743ffe2694904ab0fca9dff9febacdb236a95f0465fda8b3f3eba872d6fe34d5a3cc9752b8bee676a6db188faadadcb4d4  godot-lib.4.4.dev3.mono.template_release.aar
                       a29698f60912ec90d2a83a49a91e8fe1a3a4fdf2b6135a54d8844519aaab476dfab9329fa6701d90910b058f4e0c6f10c95237bed7c8b9f6f5311a4e29871817  Godot_v4.4-dev3_mono_export_templates.tpz
                       c17d6708c2c737e4ca9710558bdf0aacd0c84c95c502692a76b33c0c0e7a1ed37f9f9326f5d9c84498be589cd5ef0601ef3db9dc26cbefaa8b548227010747bb  Godot_v4.4-dev3_mono_linux_arm32.zip
                       6a41997b590e0908ffdbf6b26cc56f850fa0848bfa4cbebe3c67372c7c90bc07c65e846d3fdf27466dd6bc5f4dd150d6cc6bf788ca2e04ce490fcbf5aa7bf0f0  Godot_v4.4-dev3_mono_linux_arm64.zip
                       1b9f49337ff224e835371abedcc7cca282bccf3d0f93a932ab8687ac3f9f926ad09ded09d41de026b4dbe480b3b4cfbe32a30cc79c225ff1717e7a8ea4bbc64e  Godot_v4.4-dev3_mono_linux_x86_32.zip
                       2645fff411465c0c28f9862736754668e8a891a09dd961f240bc92d2dc6ae1f865510fdcefebbb17253b7940dc801e305c4d3c93dc1e127dff6550b1ebd8dc25  Godot_v4.4-dev3_mono_linux_x86_64.zip
                       c3c17766fdbe70ad68e2ffbdec4aaef3c22ccc5824fe665fdf739505fc651fda7d51660273ff48be14079bc5a787e0676eb56a681947e0ad9779bfbeb1ad42b3  Godot_v4.4-dev3_mono_macos.universal.zip
                       8898ad9634072bf129885fdd59137c29fffce5fc92fc52c89d9c63b7ac930ce5a9dffc5932cea5787c7e099e52d3a6c177abd5387be7eb19c28409777d0e4cf4  Godot_v4.4-dev3_mono_win32.zip
                       ae3aaf0f92d4dac271940ee2a4817d004f91231a17429264fd806330e143036d30348fca09f46e0c92cda752819302941a7b48a4358cc12f436d90b13352a5b7  Godot_v4.4-dev3_mono_win64.zip
                       461b48ccff128969f8c7105b9a423569325692ad48af0ed838eff216cede765775f176206e21bd9e47bcd0aa0475bab58f3583c183c99db2f5a00cf563dcdea4  Godot_v4.4-dev3_mono_windows_arm64.zip
        """;

    private static InstallationService CreateService(IReleaseManager releaseManager, IReleaseCatalog releaseCatalog)
    {
        var hostSystem = new Mock<IHostSystem>();
        var pathService = new Mock<IPathService>();
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-install-service-tests", Guid.NewGuid().ToString("N"));
        pathService.SetupGet(x => x.RootPath).Returns(rootPath);
        pathService.SetupGet(x => x.ReleasesPath).Returns(Path.Combine(Path.GetTempPath(), "releases.json"));
        pathService.SetupGet(x => x.InstallationsPath).Returns(Path.Combine(rootPath, "installations.json"));
        pathService.SetupGet(x => x.InstallationsDirectoryPath).Returns(Path.Combine(rootPath, "installations"));
        var installationRegistry = new Mock<IInstallationRegistry>();
        installationRegistry.Setup(x => x.FindByReleaseName(It.IsAny<string>()))
            .Returns(new Result<Installation, InstallationRegistryError>.Failure(new InstallationRegistryError.NotFound("test")));

        installationRegistry.Setup(x => x.UpsertInstalled(It.IsAny<Release>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>()))
            .Returns(new Result<Unit, InstallationRegistryError>.Success(Unit.Value));

        installationRegistry.Setup(x => x.SetDefault(It.IsAny<string>()))
            .Returns(new Result<Unit, InstallationRegistryError>.Success(Unit.Value));

        return new InstallationService(
            hostSystem.Object,
            releaseManager,
            releaseCatalog,
            pathService.Object,
            installationRegistry.Object,
            NullLogger<InstallationService>.Instance);
    }

    private static Mock<IReleaseManager> CreateReleaseManagerMock(params string[] releaseIds)
    {
        var releaseManager = new Mock<IReleaseManager>();

        foreach (var releaseId in releaseIds)
        {
            if (Release.TryParse($"{releaseId}-standard") is not { } release)
            {
                throw new InvalidOperationException("Expected release to parse.");
            }

            releaseManager.Setup(x => x.CreateRelease($"{releaseId}-standard"))
                .Returns(new Result<Release, ReleaseParseError>.Success(release));
        }

        return releaseManager;
    }

    private static byte[] CreateZipArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("Godot");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("fake executable");
        }

        return stream.ToArray();
    }
}
