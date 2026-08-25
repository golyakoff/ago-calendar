using Mono.Cecil;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// `adr/0027`, held as a rule instead of as prose: <b>no <c>Ago.Calendar.*</c> assembly may
/// reference any <c>Ago.Chat.*</c> assembly.</b> AGO Calendar defines its own <c>Operator</c>,
/// its own permission vocabulary and its own tables; the two products are unified only through
/// Keycloak identity, never by one reaching into the other's code or database.
///
/// <para>Unlike the platform boundary (`adr/0012`), <b>nothing makes this true by construction.</b>
/// The platform ships as a package and physically cannot see a product's source; two sibling product
/// checkouts sit next to each other on disk, and a <c>ProjectReference</c> two directories up is one
/// line away at any moment. This is the rule that has to be checked, which is why it is the one with
/// a violating fixture permanently in the build (<see cref="ChatBoundaryTests"/>).</para>
/// </summary>
internal static class ChatBoundaryRule
{
    private const string ChatAssemblyPrefix = "Ago.Chat.";

    /// <summary>
    /// The <c>Ago.Chat.*</c> assemblies this assembly's metadata says it binds against. Note the
    /// deliberate limit: the C# compiler omits an assembly reference nothing in the compiled code
    /// actually uses, so a <c>ProjectReference</c> added and never called is invisible here. That is
    /// what <see cref="ChatBoundaryTests.NoAgoChatAssembly_ReachesTheBuildOutput"/> covers instead -
    /// a referenced project is copied next to its consumer whether its types are used or not.
    /// </summary>
    public static IReadOnlyList<string> ReferencedChatAssemblies(AssemblyDefinition assembly) =>
        assembly.MainModule.AssemblyReferences
            .Select(reference => reference.Name)
            .Where(name => name.StartsWith(ChatAssemblyPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The individual <c>Ago.Chat.*</c> types this assembly names, for a failure message that says
    /// <i>what</i> was reached for rather than only which assembly it lived in.
    /// </summary>
    public static IReadOnlyList<string> ReferencedChatTypes(AssemblyDefinition assembly) =>
        assembly.MainModule.GetTypeReferences()
            .Where(type => type.Scope is AssemblyNameReference scope
                && scope.Name.StartsWith(ChatAssemblyPrefix, StringComparison.Ordinal))
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    public static bool IsChatAssemblyFile(string fileName) =>
        fileName.StartsWith(ChatAssemblyPrefix, StringComparison.Ordinal)
        && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
}
