namespace Ago.Chat.ArchFixture;

/// <summary>
/// Stands in for the tempting thing on the other side of `adr/0027`'s line -
/// <c>Ago.Chat.Domain.Operator</c>, the type AGO Calendar looks similar enough to reuse and must
/// not. Nothing here runs; being reachable from another assembly is all it has to do.
/// </summary>
public sealed record ChatSideConcept(string Name);
