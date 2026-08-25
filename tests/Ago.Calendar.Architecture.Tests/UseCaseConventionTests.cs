using NetArchTest.Rules;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// clean-architecture.md: a handler orchestrates and nothing more, so it is sealed (no subclass
/// hook to smuggle extra behaviour in through) and lives under <c>UseCases/</c>, where a reviewer
/// sees a whole feature without navigating elsewhere.
///
/// <para><c>Ago.Calendar.Application</c> has no handler yet (`20-00` builds no product behaviour),
/// so both assertions currently hold over an empty set. That is the same state `0-02` left
/// <c>Ago.Chat.Architecture.Tests</c> in, and deliberately so: the rule is in place before the first
/// use case arrives, rather than being retrofitted around code that already broke it.</para>
/// </summary>
public class UseCaseConventionTests
{
    [Fact]
    public void Handlers_AreSealed() =>
        Types.InAssembly(TestAssemblies.Application.Reflection)
            .That().HaveNameEndingWith("Handler")
            .Should().BeSealed()
            .GetResult()
            .ShouldPass("every *Handler in Ago.Calendar.Application must be sealed");

    [Fact]
    public void Handlers_LiveUnderUseCases() =>
        Types.InAssembly(TestAssemblies.Application.Reflection)
            .That().HaveNameEndingWith("Handler")
            .Should().ResideInNamespaceContaining("UseCases")
            .GetResult()
            .ShouldPass("every *Handler in Ago.Calendar.Application must live under UseCases/");
}
