using Mono.Cecil;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// NetArchTest reasons about type-level dependencies (a class having a field, parameter or base
/// type of X); it has no notion of "this one static member was called." Banning
/// <c>Guid.NewGuid()</c> specifically - not the <see cref="Guid"/> type, which ids use everywhere -
/// means reading method bodies directly, with the same library (Mono.Cecil) NetArchTest itself is
/// built on.
/// </summary>
internal static class IlMemberScanner
{
    public static IReadOnlyList<string> FindCallers(AssemblyDefinition assembly, string declaringTypeFullName, string memberName)
    {
        var offenders = new List<string>();

        foreach (var type in assembly.MainModule.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is MethodReference called
                        && called.DeclaringType.FullName == declaringTypeFullName
                        && called.Name == memberName)
                    {
                        offenders.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }
        }

        return offenders;
    }

    /// <summary>
    /// Whether an assembly mentions a type at all - in a signature, a field, a local, or a call.
    ///
    /// <para>Broader than <see cref="FindCallers"/> on purpose: `20-02`'s rule is that
    /// <c>TimeZoneInfo</c> is confined to one assembly, and confining only one *method* of it would
    /// leave a caller free to accept a resolved <c>TimeZoneInfo</c> as a parameter and do the
    /// conversion somewhere else - which is the same bug with the lookup moved. Reads the module's
    /// own TypeReference table, so it sees every mention the compiler emitted rather than only the
    /// ones in method bodies.</para>
    /// </summary>
    public static bool ReferencesType(AssemblyDefinition assembly, string typeFullName) =>
        assembly.MainModule.GetTypeReferences().Any(reference => reference.FullName == typeFullName);
}
