using System.ComponentModel;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class CompareTool
{
    [McpServerTool(Name = "compare", ReadOnly = true),
     Description("Compare two versions of an assembly, from the types that differ down to one method: " +
        "without type_name it lists the changed types, with type_name that type's member changes, and " +
        "with member_name the method's or accessor's two bodies, or only a verdict when they print alike. " +
        "A line starts with + for what only B has, - for what only A has, and ~ for what both have but " +
        "changed; a changed type names what changed. Members are matched by name, parameter types and " +
        "return type, so a changed signature is a removal and an addition. Bodies are compared as IL with " +
        "tokens resolved to names, so a method a rebuild leaves unchanged does not show as changed.")]
    public static string Compare(
        AssemblyHostRegistry registry,
        [Description(ParameterDescriptions.AssemblyA)] string assembly_a,
        [Description(ParameterDescriptions.AssemblyB)] string assembly_b,
        [Description(ParameterDescriptions.TypeName)] string type_name = null,
        [Description(ParameterDescriptions.MemberName)] string member_name = null,
        [Description(ParameterDescriptions.ParameterCount)] int? parameter_count = null,
        [Description(ParameterDescriptions.ParameterTypes)] string[] parameter_types = null,
        [Description(ParameterDescriptions.Namespace)] string @namespace = null,
        [Description(ParameterDescriptions.IncludeCompilerGenerated)] bool include_compiler_generated = false,
        [Description("How to print the two bodies: csharp decompiles them, il disassembles them as the comparison reads them.")]
        CompareFormat format = CompareFormat.Csharp,
        [Description(ParameterDescriptions.Limit)] int limit = ListOutput.DefaultLimit)
    {
        // Each parameter belongs to one level of the drill-down. Dropped silently at another,
        // it would leave the answer to a broader question looking like the answer to the one asked.
        var byType = !string.IsNullOrEmpty(type_name);
        var byMember = !string.IsNullOrEmpty(member_name);
        if (byMember && !byType)
            throw new ArgumentException($"{nameof(member_name)} needs {nameof(type_name)}.");
        if (!byMember && (parameter_count.HasValue || parameter_types != null))
            throw new ArgumentException(
                $"{nameof(parameter_count)} and {nameof(parameter_types)} select a method overload, " +
                $"so they need {nameof(member_name)}.");
        if (byType && !string.IsNullOrEmpty(@namespace))
            throw new ArgumentException($"{nameof(@namespace)} applies only without {nameof(type_name)}.");
        if (byMember && include_compiler_generated)
            throw new ArgumentException(
                $"{nameof(include_compiler_generated)} applies only without {nameof(member_name)}.");
        if (!byMember && format != CompareFormat.Csharp)
            throw new ArgumentException($"{nameof(format)} applies only with {nameof(member_name)}.");

        using var leaseA = registry.GetOrLoad(assembly_a);
        using var leaseB = registry.GetOrLoad(assembly_b);
        var comparer = new AssemblyComparer(leaseA.Host, leaseB.Host, include_compiler_generated);

        if (byMember)
            return CompareBodies(leaseA.Host, leaseB.Host, comparer, type_name, member_name,
                parameter_count, parameter_types, format);
        if (byType)
            return CompareMembers(comparer, type_name, limit);
        return CompareTypes(leaseA.Host, leaseB.Host, comparer, @namespace, limit);
    }

    private static string CompareTypes(AssemblyHost hostA, AssemblyHost hostB, AssemblyComparer comparer,
        string @namespace, int limit)
    {
        // An exact namespace asserts it exists, so a misspelling is an error rather than an
        // answer that reads as "nothing changed there".
        if (!string.IsNullOrEmpty(@namespace)
            && !hostA.Resolver.HasNamespace(@namespace) && !hostB.Resolver.HasNamespace(@namespace))
            throw new ArgumentException($"Namespace not found in either assembly: {@namespace}.");

        var lines = comparer
            .EnumerateChangedTypes(@namespace)
            .OrderBy(change => change.TypeName, StringComparer.Ordinal)
            .Select(FormatTypeChange)
            .ToList();
        return ListOutput.Format(lines, limit, new Noun("changed type", "changed types"));
    }

    private static string FormatTypeChange(AssemblyComparer.TypeChange change) => change.Presence switch
    {
        AssemblyComparer.TypePresence.Added => $"+ {change.TypeName}",
        AssemblyComparer.TypePresence.Removed => $"- {change.TypeName}",
        _ => $"~ {change.TypeName} [{FormatKinds(change.Kinds)}]",
    };

    private static string FormatKinds(AssemblyComparer.ChangeKinds kinds)
    {
        var parts = new List<string>(3);
        if ((kinds & AssemblyComparer.ChangeKinds.Metadata) != 0) parts.Add("metadata");
        if ((kinds & AssemblyComparer.ChangeKinds.Members) != 0) parts.Add("members");
        if ((kinds & AssemblyComparer.ChangeKinds.Bodies) != 0) parts.Add("bodies");
        return string.Join(",", parts);
    }

    private static string CompareMembers(AssemblyComparer comparer, string typeName, int limit)
    {
        var diff = comparer.CompareType(typeName);
        var lines = diff.Changes
            .OrderBy(change => change.Name, StringComparer.Ordinal)
            .ThenBy(change => change.Signature, StringComparer.Ordinal)
            .Select(change => change.Kind switch
            {
                AssemblyComparer.MemberChangeKind.Added => $"+ {change.Signature}",
                AssemblyComparer.MemberChangeKind.Removed => $"- {change.Signature}",
                // A property or event names the accessors whose bodies changed, which are what
                // member_name takes to show the two bodies.
                _ => change.Accessors is { Count: > 0 } accessors
                    ? $"~ {change.Signature} [{string.Join(",", accessors)}]"
                    : $"~ {change.Signature}",
            })
            .ToList();
        var subject = diff.Side switch
        {
            AssemblyComparer.TypePresence.Added => $"in {diff.TypeName}, which only B has",
            AssemblyComparer.TypePresence.Removed => $"in {diff.TypeName}, which only A has",
            _ => $"in {diff.TypeName}",
        };
        return ListOutput.Format(lines, limit, new Noun("member change", "member changes"), subject);
    }

    private static string CompareBodies(AssemblyHost hostA, AssemblyHost hostB, AssemblyComparer comparer,
        string typeName, string memberName, int? parameterCount, string[] parameterTypes, CompareFormat format)
    {
        var (aMethod, bMethod, aType, bType) =
            comparer.ResolveMethodPair(typeName, memberName, parameterCount, parameterTypes);
        if (aMethod is null && bMethod is null)
        {
            // Neither assembly has such a method, and decompile's lookup says why. Only a method or
            // an accessor has a body. A property or event is compared through its accessors, and a
            // field has none, so either is an error that says where to look instead.
            var (host, type) = aType is not null ? (hostA, aType) : (hostB, bType);
            var (symbol, _, _) = host.Resolver.ResolveMember(type, memberName, parameterCount, parameterTypes);
            throw new ArgumentException(NotAMethod(symbol));
        }

        var reference = ReferenceFormatter.FormatResolvedSignature(
            aMethod ?? bMethod, ReferenceFormatter.FormatTypeName(aType ?? bType));

        // Burst turns a method it compiles into a call to its wrapper and compiles a copy of the
        // method's code, so the copy is the C# to show.
        IMethod CopyToShow(AssemblyHost host, IMethod method) =>
            format == CompareFormat.Il ? null : AssemblyComparer.BurstManagedCopyOf(host, method);
        string CodeToShow(AssemblyHost host, IMethod method, IMethod copy) =>
            format == CompareFormat.Il ? AssemblyComparer.CodeOf(host, method) : host.DecompileMethod(copy ?? method);

        // A method only one assembly has is the whole difference, so its code is shown alone.
        if (aMethod is null || bMethod is null)
        {
            var (side, host, method) = aMethod is null ? ("B", hostB, bMethod) : ("A", hostA, aMethod);
            var copy = CopyToShow(host, method);
            return $"// {reference}{Via(copy)} — only {side} has it.\n\n{CodeToShow(host, method, copy)}";
        }

        var aCopy = CopyToShow(hostA, aMethod);
        var bCopy = CopyToShow(hostB, bMethod);
        var via = Via(aCopy ?? bCopy);

        // Two bodies that print alike are answered by the verdict alone: a copy would show nothing
        // of what changed, and decompile returns the code. The verdict is the one the member list
        // gives, which also counts the code generated for the method; the C# can read alike where
        // the IL does not.
        if (comparer.HaveEqualCode(aMethod, bMethod))
            return $"// {reference}{via} — bodies are identical in both assemblies.";
        var aBody = CodeToShow(hostA, aMethod, aCopy);
        var bBody = CodeToShow(hostB, bMethod, bCopy);
        if (aBody == bBody)
            return $"// {reference}{via} — bodies decompile alike in both assemblies, but their IL differs; " +
                "format il shows where.";
        return (via.Length > 0 ? $"// {reference}{via}\n\n" : "") +
            $"// === A: {hostA.AssemblyPath} ===\n{aBody}\n\n" +
            $"// === B: {hostB.AssemblyPath} ===\n{bBody}";
    }

    private static string Via(IMethod copy) => copy is null ? "" : $" (via {copy.Name})";

    private static string NotAMethod(ISymbol symbol)
    {
        var name = ReferenceFormatter.FormatSymbol(symbol);
        string Accessors(params IMethod[] accessors) =>
            string.Join(", ", accessors.Where(accessor => accessor != null).Select(accessor => accessor.Name));
        return symbol switch
        {
            IProperty property =>
                $"{name} is a property; compare its accessors, {Accessors(property.Getter, property.Setter)}.",
            IEvent @event =>
                $"{name} is an event; compare its accessors, {Accessors(@event.AddAccessor, @event.RemoveAccessor)}.",
            _ => $"{name} is a field, which has no body; an initializer it has is in the constructors, .ctor.",
        };
    }
}
