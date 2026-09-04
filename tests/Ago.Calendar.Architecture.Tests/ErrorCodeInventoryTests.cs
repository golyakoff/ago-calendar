using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// `22-20`: the set-mismatch that let sixteen constructed error codes fall through
/// <c>ErrorExtensions.ToProblem</c>'s switch and reach an operator as a bare 500, found only because
/// someone happened to read the file for another reason - the same way `22-15`'s four dead arms
/// (mapped codes with no producer left, after <c>access.*</c> was deleted) were found. Both are one
/// check: the set of codes any method actually constructs an <see cref="Error"/> with, against the
/// set of codes the switch names.
///
/// <para><b>Read from IL, like <see cref="IlMemberScanner"/>, not from the <c>.cs</c> source text.</b>
/// A source-text regex would need to keep pace with every shape a producer might be written in
/// (<c>new("code", msg)</c>, <c>new Error("code", msg)</c>, one day maybe a helper method) and would
/// also need the repository's file layout to be reachable at test time, which the other checks in
/// this project deliberately do not depend on (`TestAssemblies`' own remarks: every assembly here is
/// loaded from build output, not source, for exactly this reason). IL sidesteps both: whichever
/// syntax a producer is written in, <c>Error</c> being a record struct means constructing one always
/// lowers to <c>newobj instance void Ago.Platform.Kernel.Error::.ctor(string, string)</c>, and a
/// string-switch over <c>error.Code</c> always lowers to a chain of <c>ldstr</c>/equality checks
/// naming every case label. Scanning for those two shapes sees exactly what the compiler produced -
/// including the five ad hoc <c>new Error(...)</c> call sites outside any <c>*Errors</c> class
/// (<c>ConsoleEndpoints</c>, <c>EditDayBoundaryHandler</c>, <c>RegisterTenantHandler</c>) that a
/// scan restricted to <c>*Errors.cs</c> factory methods would have missed entirely - which is the
/// false-negative shape CLAUDE.md's own note on `ago-chat`'s `17-12` warns a guard can have without
/// being any less confident-looking.</para>
///
/// <para><b>What this still cannot see.</b> The heuristic that turns a bare <c>ldstr</c> into "this is
/// a code, not a message fragment" is shape, not semantics: a code looks like
/// <c>^[a-z][a-z_]*(\.[a-z][a-z_]*)+$</c> (lowercase words and underscores, joined by dots) and every
/// message in this codebase is a human sentence that cannot fully match that pattern - checked by
/// eye against every producer as of `22-20`, not proven for all future ones. A message literal that
/// somehow collapsed to that exact shape would be misread as a code; a code built at runtime by
/// string concatenation rather than as one literal would be invisible to it, the same way it would be
/// invisible to a source-text scan. Both are judged acceptable because every producer in this
/// codebase today writes the code as one literal, which is also this file's own convention
/// (`RecutErrors`'s own remarks: "the <c>&lt;area&gt;.&lt;reason&gt;</c> vocabulary every other use
/// case here uses").</para>
/// </summary>
public class ErrorCodeInventoryTests
{
    private const string ErrorTypeFullName = "Ago.Platform.Kernel.Error";

    private static readonly Regex CodeShape = new(
        @"^[a-z][a-z_]*(\.[a-z][a-z_]*)+$", RegexOptions.Compiled);

    [Fact]
    public void EveryConstructedErrorCode_IsMappedInErrorExtensions()
    {
        var produced = ProducedCodes();
        var mapped = MappedCodes();

        var unmapped = produced.Except(mapped).OrderBy(code => code, StringComparer.Ordinal).ToList();

        Assert.True(unmapped.Count == 0,
            "Ago.Calendar.Api.Http.ErrorExtensions.ToProblem has no arm for: " +
            string.Join(", ", unmapped) +
            " - every constructed Error code needs an argued status, not the 500 the catch-all gives.");
    }

    /// <summary>The mirror direction `22-15` fixed by hand: a mapped code with no producer left is not
    /// a bug the operator ever meets, but it is evidence for a status nobody can defend, and the switch
    /// is exactly the file whose own comments this item leaned on to argue the sixteen new arms.</summary>
    [Fact]
    public void EveryMappedErrorCode_HasAProducer()
    {
        var produced = ProducedCodes();
        var mapped = MappedCodes();

        var orphaned = mapped.Except(produced).OrderBy(code => code, StringComparer.Ordinal).ToList();

        Assert.True(orphaned.Count == 0,
            "Ago.Calendar.Api.Http.ErrorExtensions.ToProblem names a code nothing constructs: " +
            string.Join(", ", orphaned) + " - dead arms read as evidence something still produces them.");
    }

    /// <summary>Every string literal, in every method of every scanned assembly, that both (a) shares a
    /// method body with a <c>newobj Error::.ctor</c> and (b) is shaped like a code rather than a
    /// message. (a) bounds the scan to methods that actually build an <see cref="Error"/> at all, so a
    /// coincidentally code-shaped literal used for something unrelated elsewhere in the product could
    /// not be picked up as a false positive.</summary>
    private static HashSet<string> ProducedCodes()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);

        var scanned = TestAssemblies.AllProduct
            .Concat(TestAssemblies.ServingHosts)
            .Append(TestAssemblies.Migrator)
            .DistinctBy(assembly => assembly.Name);

        foreach (var assembly in scanned)
        {
            foreach (var type in assembly.Cecil.MainModule.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    if (!method.Body.Instructions.Any(IsErrorConstruction))
                    {
                        continue;
                    }

                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode == OpCodes.Ldstr
                            && instruction.Operand is string literal
                            && CodeShape.IsMatch(literal))
                        {
                            codes.Add(literal);
                        }
                    }
                }
            }
        }

        return codes;
    }

    /// <summary>Every code-shaped string literal in <c>ErrorExtensions</c> itself - the switch's own
    /// case labels, read the same way <see cref="ProducedCodes"/> reads a producer: as IL, not source
    /// text, so this does not care whether Roslyn lowers a 40-arm string switch to sequential equality
    /// checks or a hash-bucketed jump table - both still carry every case label as a literal
    /// <c>ldstr</c> somewhere in the method that switches on it.</summary>
    private static HashSet<string> MappedCodes()
    {
        var errorExtensions = TestAssemblies.Api.Cecil.MainModule.GetTypes()
            .Single(t => t.FullName == "Ago.Calendar.Api.Http.ErrorExtensions");

        var codes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in errorExtensions.Methods.Where(m => m.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode == OpCodes.Ldstr
                    && instruction.Operand is string literal
                    && CodeShape.IsMatch(literal))
                {
                    codes.Add(literal);
                }
            }
        }

        return codes;
    }

    private static bool IsErrorConstruction(Instruction instruction) =>
        instruction.OpCode == OpCodes.Newobj
        && instruction.Operand is MethodReference constructor
        && constructor.DeclaringType.FullName == ErrorTypeFullName;
}
