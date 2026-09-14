using Mono.Cecil;

namespace Ago.Calendar.Infrastructure.TenantScopeDiagnostics;

/// <summary>
/// `24-17`: this product's own copy of `ago-chat`'s `Ago.Chat.Infrastructure.TenantScopeDiagnostics.TenantScopeRule`
/// - the identical IL-level question ("does this entry point take the tenant id and gate it through
/// the permission port"), asked of `Ago.Calendar.Application`'s own handlers and its own two type
/// names.
///
/// <para><b>A copy, not a shared library, and not merely for lack of time to build one.</b> `adr/0027`
/// already settled the general question for this exact kind of code - each product carries its own
/// version of a mechanism the two happen to shape alike, rather than depending on a package that would
/// make one repository's build see into the other's decisions. Reusing `ago-chat`'s own class directly
/// would require this product's Api to reference an `ago-chat` assembly, exactly the cross-repository
/// build dependency the backlog item's own "Why runtime is more honest" section says the runtime shape
/// was chosen specifically to avoid needing in `ago-calendar`.</para>
///
/// <para><b>One real difference from the class this was copied from:</b> `Ago.Calendar.Application.Abstractions.IPermissionChecker`
/// declares one method, <c>HasPermissionAsync</c> - unlike `ago-chat`'s interface, this product has no
/// `GetPermissionsAsync`-shaped second method, so <see cref="ChecksPermission"/> only ever needs to
/// look for the one call.</para>
/// </summary>
public static class CalendarTenantScopeRule
{
    private const string PermissionCheckerInterface = "Ago.Calendar.Application.Abstractions.IPermissionChecker";

    private const string TenantIdType = "Ago.Calendar.Domain.TenantId";

    /// <summary>One public entry point of one handler, and the two facts the rule turns on.</summary>
    public sealed record EntryPoint(string Key, bool CarriesTenantId, bool ChecksPermission)
    {
        public bool IsRbacGated => CarriesTenantId && ChecksPermission;
    }

    /// <summary>Every public entry point of every <c>*Handler</c> in <paramref name="assembly"/>,
    /// keyed <c>Namespace.Type.Method</c>.</summary>
    public static IReadOnlyList<EntryPoint> Scan(AssemblyDefinition assembly) =>
    [
        .. from type in assembly.MainModule.GetTypes()
           where type.IsClass && type.Name.EndsWith("Handler", StringComparison.Ordinal) && !IsCompilerGenerated(type)
           from method in type.Methods
           where IsEntryPoint(method)
           select new EntryPoint(
               $"{type.FullName}.{method.Name}",
               CarriesTenantId(method),
               CallsPermissionChecker(type, method)),
    ];

    private static bool IsEntryPoint(MethodDefinition method) =>
        method.IsPublic
        && !method.IsConstructor
        && !method.IsGetter
        && !method.IsSetter
        && !IsCompilerGenerated(method);

    /// <summary>A parameter that is a <c>TenantId</c>, or a command/query record with a
    /// <c>TenantId</c>-typed member - one level, no recursion, the identical shallowness `ago-chat`'s
    /// own rule documents for itself and for the identical reason: every command and query in this
    /// product is a flat record of primitives and strongly-typed ids.</summary>
    private static bool CarriesTenantId(MethodDefinition method) =>
        method.Parameters.Any(parameter =>
            parameter.ParameterType.FullName == TenantIdType || HasTenantIdMember(parameter.ParameterType));

    private static bool HasTenantIdMember(TypeReference parameterType)
    {
        var resolved = TryResolve(parameterType);
        if (resolved is null)
        {
            return false;
        }

        return resolved.Properties.Any(p => p.PropertyType.FullName == TenantIdType)
            || resolved.Fields.Any(f => f.FieldType.FullName == TenantIdType);
    }

    private static bool CallsPermissionChecker(TypeDefinition handler, MethodDefinition method) =>
        BodiesOf(handler, method).Any(body => body.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference called
            && called.DeclaringType.FullName == PermissionCheckerInterface));

    /// <summary>The method's own body plus, for an <c>async</c> method, its compiler-generated state
    /// machine's - see `ago-chat`'s own `TenantScopeRule` remarks on why that indirection is
    /// unavoidable here too (the identical C# compiler, the identical `async`/await lowering).</summary>
    private static IEnumerable<MethodDefinition> BodiesOf(TypeDefinition handler, MethodDefinition method)
    {
        if (method.HasBody)
        {
            yield return method;
        }

        var stateMachine = method.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == "AsyncStateMachineAttribute");
        if (stateMachine?.ConstructorArguments.Count is not > 0
            || stateMachine.ConstructorArguments[0].Value is not TypeReference machineType)
        {
            yield break;
        }

        var resolved = handler.NestedTypes.FirstOrDefault(t => t.FullName == machineType.FullName)
            ?? TryResolve(machineType);
        if (resolved is null)
        {
            yield break;
        }

        foreach (var machineMethod in resolved.Methods.Where(m => m.HasBody))
        {
            yield return machineMethod;
        }
    }

    private static TypeDefinition? TryResolve(TypeReference reference)
    {
        try
        {
            return reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private static bool IsCompilerGenerated(ICustomAttributeProvider member) =>
        member.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute");
}
