namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// `adr/0027` in code. See <see cref="ChatBoundaryRule"/> for why this rule, alone among the ones
/// on this page, is not already true by construction.
/// </summary>
public class ChatBoundaryTests
{
    /// <summary>
    /// The one assembly file allowed to match <c>Ago.Chat.*.dll</c> in a build output here: the
    /// stand-in this project's own fixtures reference in order to prove the rule can go red
    /// (<see cref="TheRule_FlagsAnAssemblyThatReachesIntoChat"/>). It carries one type and no
    /// behaviour; the name is the entire point of it.
    /// </summary>
    private const string FixtureStandIn = "Ago.Chat.ArchFixture.dll";

    [Fact]
    public void NoProductAssembly_ReferencesAnAgoChatAssembly()
    {
        var offenders = TestAssemblies.AllProduct
            .SelectMany(assembly => ChatBoundaryRule.ReferencedChatAssemblies(assembly.Cecil)
                .Select(chat => $"{assembly.Name} -> {chat}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "adr/0027: AGO Calendar has its own Operator, its own permissions and its own tables - no "
            + "Ago.Calendar.* assembly may bind against an Ago.Chat.* one. Offending references: "
            + string.Join("; ", offenders));
    }

    [Fact]
    public void NoProductAssembly_NamesAnAgoChatType()
    {
        var offenders = TestAssemblies.AllProduct
            .SelectMany(assembly => ChatBoundaryRule.ReferencedChatTypes(assembly.Cecil)
                .Select(type => $"{assembly.Name} -> {type}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "adr/0027: an Ago.Calendar.* assembly names a type from AGO Chat. Offending types: "
            + string.Join("; ", offenders));
    }

    /// <summary>
    /// The reference-level checks above read compiled metadata, and the C# compiler drops an
    /// assembly reference whose types nothing uses - so a <c>ProjectReference</c> into
    /// <c>../ago-chat</c> that has not been called *yet* would slip past them while sitting in the
    /// .csproj waiting for its first caller. A referenced project is still copied next to its
    /// consumer, used or not, so the build output is where that shows up.
    /// </summary>
    [Fact]
    public void NoAgoChatAssembly_ReachesTheBuildOutput()
    {
        var offenders = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(ChatBoundaryRule.IsChatAssemblyFile)
            .Where(file => !string.Equals(file, FixtureStandIn, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "adr/0027: an Ago.Chat.* assembly was copied into this solution's build output, so something "
            + $"in it references ago-chat. Found: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// <b>The rule, proven able to fail</b>, the way `0-02` demonstrated its layering rules and
    /// `17-01`'s <c>TenantScopeTests</c> kept the demonstration in the build rather than performing
    /// it once by hand.
    ///
    /// <para>A real violation cannot be committed here - it would need an <c>Ago.Chat.*</c> binary
    /// inside this repository, which is precisely what the rule forbids. So the fixtures build a
    /// faithful stand-in instead: <c>Ago.Chat.ArchFixture</c> is a one-type assembly whose only
    /// distinguishing property is its name, and <c>Ago.Calendar.ArchFixture.ReachesIntoChat</c>
    /// genuinely compiles against it - a real <c>ProjectReference</c>, a real assembly reference in
    /// real IL, not a hand-written metadata table. Its twin
    /// <c>Ago.Calendar.ArchFixture.Compliant</c> is the same file with that one line removed, so a
    /// rule that flagged everything would fail here too.</para>
    /// </summary>
    [Fact]
    public void TheRule_FlagsAnAssemblyThatReachesIntoChat()
    {
        var violating = TestAssemblies.LoadFixture("Ago.Calendar.ArchFixture.ReachesIntoChat");
        var compliant = TestAssemblies.LoadFixture("Ago.Calendar.ArchFixture.Compliant");

        Assert.Equal(
            ["Ago.Chat.ArchFixture"],
            ChatBoundaryRule.ReferencedChatAssemblies(violating));
        Assert.Contains(
            "Ago.Chat.ArchFixture.ChatSideConcept",
            ChatBoundaryRule.ReferencedChatTypes(violating));

        Assert.Empty(ChatBoundaryRule.ReferencedChatAssemblies(compliant));
        Assert.Empty(ChatBoundaryRule.ReferencedChatTypes(compliant));
    }
}
