using System.Reflection;
using Ago.Calendar.Contracts;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `20-24`: the parsing that turns a compiled assembly's informational version into "which commit is
/// this" - ported from <c>Ago.Chat.Application.Tests.BuildInfoResponseTests</c> (`15-06`), same cases,
/// this product's own contract. Tested here rather than through a live host for the same reason that
/// suite gives: the only interesting behaviour is the string handling - the endpoint that exposes it
/// is one line with no branch in it, and standing up a <c>WebApplicationFactory</c> would exercise
/// ASP.NET's routing, not this item's own logic.
/// </summary>
public sealed class BuildInfoResponseTests
{
    [Fact]
    public void Parse_WithTheShapeTheSdkEmits_SplitsVersionFromCommit()
    {
        // What `dotnet publish -p:SourceRevisionId=<sha>` actually produces.
        var info = BuildInfoResponse.Parse("Ago.Calendar.Api", "1.0.0+433847f0e1c2b3a4d5e6f708192a3b4c5d6e7f80");

        Assert.Equal("Ago.Calendar.Api", info.Host);
        Assert.Equal("1.0.0", info.Version);
        Assert.Equal("433847f0e1c2b3a4d5e6f708192a3b4c5d6e7f80", info.Commit);
    }

    [Fact]
    public void Parse_WithAPreReleaseVersion_KeepsTheHyphenatedPartWithTheVersion()
    {
        // SemVer: '-' introduces the pre-release part and belongs to the version; only '+' introduces
        // build metadata. Splitting on the wrong one would silently report a commit of "rc.1+<sha>".
        var info = BuildInfoResponse.Parse("Ago.Calendar.Worker", "1.2.0-rc.1+abcdef1234567890");

        Assert.Equal("1.2.0-rc.1", info.Version);
        Assert.Equal("abcdef1234567890", info.Commit);
    }

    [Fact]
    public void Parse_WithNoRevisionSuffix_ReportsTheCommitAsUnknown()
    {
        // A plain `dotnet run`, or an image built without --build-arg GIT_COMMIT.
        var info = BuildInfoResponse.Parse("Ago.Calendar.Api", "1.0.0");

        Assert.Equal("1.0.0", info.Version);
        Assert.Equal(BuildInfoResponse.UnknownCommit, info.Commit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_WithNoInformationalVersionAtAll_ReportsUnknownRatherThanEmpty(string? informational)
    {
        // A deployment that cannot name its own commit has to say so; an empty string in the response
        // reads as "no commit", which is the ambiguity this item exists to remove.
        var info = BuildInfoResponse.Parse("Ago.Calendar.Api", informational);

        Assert.Equal(BuildInfoResponse.UnknownCommit, info.Version);
        Assert.Equal(BuildInfoResponse.UnknownCommit, info.Commit);
    }

    [Fact]
    public void Parse_WithATrailingPlusAndNothingAfterIt_ReportsUnknownRatherThanEmpty()
    {
        var info = BuildInfoResponse.Parse("Ago.Calendar.Api", "1.0.0+");

        Assert.Equal("1.0.0", info.Version);
        Assert.Equal(BuildInfoResponse.UnknownCommit, info.Commit);
    }

    [Fact]
    public void For_ReadsTheHostNameAndInformationalVersionOffARealAssembly()
    {
        var assembly = typeof(BuildInfoResponse).Assembly;

        var info = BuildInfoResponse.For(assembly);

        Assert.Equal(assembly.GetName().Name, info.Host);
        Assert.Equal(
            BuildInfoResponse.Parse(info.Host, assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion),
            info);
    }

    [Fact]
    public void For_WithNoAssembly_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BuildInfoResponse.For(null!));
    }
}
