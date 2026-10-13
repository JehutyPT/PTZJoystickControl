using Octokit;
using PtzJoystickControl.Application.Services;
using Xunit;

namespace PtzJoystickControl.Tests.Services;

public class UpdateServiceTests
{
    // Assembly version of a build CI produced from csproj 0.0.23.43; its release is tagged v0.0.23.
    private static readonly Version CurrentBuild = new(0, 0, 23, 43);

    [Fact]
    public void FindUpdate_NewerPrerelease_IsAvailableWithItsTagAndPage()
    {
        var update = UpdateService.FindUpdate(
            [Release("v0.0.24", prerelease: true), Release("v0.0.23", prerelease: true)],
            CurrentBuild);

        Assert.True(update.Available);
        Assert.False(update.Error);
        Assert.Equal("v0.0.24", update.Version);
        Assert.Equal(new Uri("https://github.com/JehutyPT/PTZJoystickControl/releases/tag/v0.0.24"), update.Uri);
    }

    [Fact]
    public void FindUpdate_ReleaseOfTheRunningBuild_IsNotAnUpdate()
    {
        var update = UpdateService.FindUpdate([Release("v0.0.23", prerelease: true)], CurrentBuild);

        Assert.False(update.Available);
        Assert.False(update.Error);
        Assert.Equal("v0.0.23", update.Version);
    }

    [Fact]
    public void FindUpdate_OnlyOlderReleases_IsNotAnUpdate()
    {
        var update = UpdateService.FindUpdate(
            [Release("v0.0.22", prerelease: true), Release("v0.0.21", prerelease: true)],
            CurrentBuild);

        Assert.False(update.Available);
        Assert.Equal("v0.0.22", update.Version);
    }

    [Fact]
    public void FindUpdate_PicksHighestVersionWhateverTheListOrder()
    {
        var update = UpdateService.FindUpdate(
            [Release("v0.0.9"), Release("v0.0.30"), Release("v0.0.24"), Release("v0.0.100"), Release("v0.0.25")],
            CurrentBuild);

        Assert.True(update.Available);
        Assert.Equal("v0.0.100", update.Version);
    }

    [Fact]
    public void FindUpdate_IgnoresDrafts()
    {
        var update = UpdateService.FindUpdate(
            [Release("v0.0.99", draft: true), Release("v0.0.23")],
            CurrentBuild);

        Assert.False(update.Available);
        Assert.Equal("v0.0.23", update.Version);
    }

    [Theory]
    [InlineData("0.0.24")]
    [InlineData("v0.0.24")]
    [InlineData("v0.0.24.50")]
    [InlineData(" v0.0.24 ")]
    public void FindUpdate_AcceptsCiTagFormats(string tag)
    {
        var update = UpdateService.FindUpdate([Release(tag)], CurrentBuild);

        Assert.True(update.Available);
        Assert.Equal(tag, update.Version);
    }

    [Fact]
    public void FindUpdate_SkipsUnparsableTags()
    {
        var update = UpdateService.FindUpdate(
            [Release("nightly"), Release("v1.2.3-beta"), Release("release-9.9.9"), Release("v0.0.24")],
            CurrentBuild);

        Assert.True(update.Available);
        Assert.Equal("v0.0.24", update.Version);
    }

    [Fact]
    public void FindUpdate_NoReleases_IsNotAnUpdateAndSaysWhy()
    {
        var update = UpdateService.FindUpdate([], CurrentBuild);

        Assert.False(update.Available);
        Assert.True(update.Error);
        Assert.False(string.IsNullOrWhiteSpace(update.ErrorMessage));
    }

    [Fact]
    public void FindUpdate_NoParsableNonDraftRelease_IsNotAnUpdateAndSaysWhy()
    {
        var update = UpdateService.FindUpdate(
            [Release("nightly"), Release("v0.0.99", draft: true)],
            CurrentBuild);

        Assert.False(update.Available);
        Assert.True(update.Error);
    }

    [Fact]
    public void FindUpdate_NewestReleaseWithoutUsablePage_IsNotAnUpdate()
    {
        var update = UpdateService.FindUpdate([Release("v0.0.24", htmlUrl: "not a url")], CurrentBuild);

        Assert.False(update.Available);
        Assert.True(update.Error);
    }

    private static Release Release(string tag, bool prerelease = false, bool draft = false, string? htmlUrl = null) =>
        new(
            url: "",
            htmlUrl: htmlUrl ?? $"https://github.com/JehutyPT/PTZJoystickControl/releases/tag/{tag}",
            assetsUrl: "",
            uploadUrl: "",
            id: 1,
            nodeId: "",
            tagName: tag,
            targetCommitish: "main",
            name: tag,
            body: "",
            draft: draft,
            prerelease: prerelease,
            createdAt: DateTimeOffset.UnixEpoch,
            publishedAt: DateTimeOffset.UnixEpoch,
            author: null,
            tarballUrl: "",
            zipballUrl: "",
            assets: []);
}
