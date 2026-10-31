using System.Text;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// analyze's answer for kind call_sites: every call to a method, with what each call passes.
/// </summary>
public static class CallSiteReport
{
    private static readonly Noun Call = Noun.Regular("call");
    private static readonly Noun AttributeUse = Noun.Regular("use");
    private static readonly Noun Member = Noun.Regular("member");

    /// <summary>
    /// Every call to the overloads of <paramref name="methodName"/> on <paramref name="type"/>,
    /// or to the one a hint selects, each on a line beneath the member that makes it, which is
    /// named once. With <paramref name="parameterName"/>, a call's line shows only the argument
    /// for that parameter, and <paramref name="nonDefaultOnly"/> keeps the calls whose argument
    /// is not provably the parameter's default.
    /// </summary>
    public static string Format(AssemblyHost host, ITypeDefinition type, string methodName,
        SymbolResolver.MethodHint hint, string parameterName, bool nonDefaultOnly, int limit)
    {
        RequireParameterName(parameterName, nonDefaultOnly);
        var targets = host.Resolver.ResolveMethodOverloads(type, methodName, hint);
        // An extension method found while a base class of the type was not read may stand where
        // a member of that class would answer, which the last line says.
        var notChecked = targets.Select(target => host.Resolver.ExtensionNotChecked(type, target))
            .FirstOrDefault(line => line is not null) is { } line ? "\n" + line : "";
        return Format(host, targets, ReferenceFormatter.FormatTypeName(type),
            method => host.Resolver.Namesakes(type, method).OfType<IMethod>(), parameterName, nonDefaultOnly, limit) + notChecked;
    }

    /// <summary>
    /// <see cref="Format(AssemblyHost, ITypeDefinition, string, SymbolResolver.MethodHint, string, bool, int)"/>
    /// for <paramref name="targets"/>, the overloads a lookup found on the type
    /// <paramref name="typeDisplay"/> names, of which <paramref name="namesakes"/> gives the
    /// methods one is told apart from where it is named alone.
    /// </summary>
    public static string Format(AssemblyHost host, IReadOnlyList<IMethod> targets, string typeDisplay,
        Func<IMethod, IEnumerable<IMethod>> namesakes, string parameterName, bool nonDefaultOnly, int limit)
    {
        RequireParameterName(parameterName, nonDefaultOnly);
        var byParameter = !string.IsNullOrEmpty(parameterName);
        // The name the overloads have, which for a constructor asked for by its type's name is .ctor.
        var methodName = targets[0].Name;
        if (byParameter)
            targets = OverloadsWith(targets, parameterName, nonDefaultOnly, typeDisplay, methodName);

        // One method is named in full where another of its name would read alike.
        var subject = targets.Count == 1
            ? ReferenceFormatter.NameAmong(targets[0], namesakes(targets[0]),
                (method, full) => ReferenceFormatter.FormatMethodReference(method, full))
            : $"{typeDisplay}.{methodName} ({targets.Count} overloads)";
        var result = CallSiteFinder.Find(host, targets);
        var routes = string.Concat(targets
            .SelectMany(target => UsageRoutes.For(host, target, listsCalls: true))
            .Distinct()
            .Select(route => "\n" + route));
        // Applying an attribute passes its constructor arguments without any call, and sets a
        // property it names through the setter without one, so no count of calls shows either,
        // and without this line "No calls" would read as nothing passing the constructor or
        // the setter anything.
        var usesAsAttribute = host.CountAttributeUses(targets.Where(target => target.IsConstructor && !target.IsStatic))
            + host.CountNamedArgumentUses(targets
                .Where(target => target.AccessorKind == System.Reflection.MethodSemanticsAttributes.Setter)
                .Select(target => target.AccessorOwner)
                .OfType<IProperty>());
        var notShownAsAttribute = usesAsAttribute == 0
            ? ""
            : $"\nNot shown: {AttributeUse.Count(usesAsAttribute)} as an attribute; " +
              $"{AnalysisKind.UsedBy.ToSchemaName()} lists them";
        if (result.Calls.Count == 0 && result.Shortfalls.Count == 0)
            return ListOutput.Header(0, Call, "to " + subject) + routes + notShownAsAttribute;

        // The parameter is named as a declaration line writes it, with the '@' C# writes before a keyword.
        var written = ReferenceFormatter.FormatIdentifier(parameterName);
        var calls = result.Calls;
        string header;
        if (nonDefaultOnly)
        {
            calls = calls.Where(call => !ArgumentFor(call, parameterName).IsDefault).ToList();
            header = $"{calls.Count} of {Call.Count(result.Calls.Count)} to {subject} " +
                $"{(calls.Count == 1 ? "passes" : "pass")} {written} other than its " +
                $"default{SharedDefault(targets, parameterName)}:";
        }
        else if (byParameter)
        {
            header = $"{Call.Count(calls.Count)} to {subject}, argument for {written}:";
        }
        else
        {
            header = $"{Call.Count(calls.Count)} to {subject}:";
        }

        string Passed(CallSiteFinder.CallSite call)
        {
            if (!byParameter)
                return call.Call;
            var argument = ArgumentFor(call, parameterName);
            return argument.IsDefault && !nonDefaultOnly ? argument.Text + " (default)" : argument.Text;
        }

        // The finder orders the calls by caller, so those of one caller stand together.
        var sb = new StringBuilder(header).Append(ListOutput.Grouped(
            calls.Select(call => (result.CallerName(call.Caller), Passed(call))).ToList(), limit));

        sb.Append(FormatShortfalls(result.Shortfalls, result.CallerName, limit)).Append(routes).Append(notShownAsAttribute);

        return sb.ToString();
    }

    /// <summary>
    /// The overloads that declare <paramref name="parameterName"/>. Throws when none does, and,
    /// for <paramref name="nonDefaultOnly"/>, when one declares it without a default, since
    /// there would be nothing to compare its arguments with.
    /// </summary>
    private static IReadOnlyList<IMethod> OverloadsWith(IReadOnlyList<IMethod> overloads,
        string parameterName, bool nonDefaultOnly, string typeDisplay, string methodName)
    {
        // A parameter is named as a declaration line writes it, with the '@' C# writes before a keyword.
        var written = ReferenceFormatter.FormatIdentifier(parameterName);
        var withParameter = overloads
            .Where(method => method.Parameters.Any(p => p.Name == parameterName))
            .ToList();
        if (withParameter.Count == 0)
        {
            var names = overloads.SelectMany(method => method.Parameters)
                .Select(p => ReferenceFormatter.FormatIdentifier(p.Name)).Distinct();
            throw new ArgumentException(
                $"No overload of '{methodName}' on {typeDisplay} has a parameter named " +
                $"'{written}'. Its parameters: {string.Join(", ", names)}.");
        }

        if (nonDefaultOnly)
        {
            var required = withParameter.FirstOrDefault(method =>
                !method.Parameters.First(p => p.Name == parameterName).IsOptional);
            if (required != null)
                throw new ArgumentException(
                    $"'{written}' has no default value in " +
                    $"{ReferenceFormatter.FormatMethodReference(required)}, so {ParameterNames.NonDefaultOnly} has " +
                    "nothing to compare its arguments with.");
        }
        return withParameter;
    }

    /// <summary>
    /// Throws for <paramref name="nonDefaultOnly"/> without <paramref name="parameterName"/>,
    /// which it compares the argument for, before any lookup answers for the method.
    /// </summary>
    private static void RequireParameterName(string parameterName, bool nonDefaultOnly)
    {
        if (nonDefaultOnly && string.IsNullOrEmpty(parameterName))
            throw new ArgumentException(
                $"{ParameterNames.NonDefaultOnly} needs {ParameterNames.ParameterName}: it compares the argument " +
                "passed for that parameter with the parameter's default.");
    }

    private static CallSiteFinder.Argument ArgumentFor(CallSiteFinder.CallSite call, string parameterName) =>
        call.Arguments.First(argument => argument.Parameter.Name == parameterName);

    /// <summary>
    /// " 0" when every overload gives the parameter the same default, so the header can name
    /// it; empty otherwise, since no single value would be true for all of them. The defaults are
    /// compared as written in full, so members of two enums of one short name are no shared one.
    /// </summary>
    private static string SharedDefault(IReadOnlyList<IMethod> targets, string parameterName)
    {
        var defaults = targets
            .Select(method => method.Parameters.First(p => p.Name == parameterName))
            .Select(parameter => (Short: ConstantFormatter.DefaultOf(parameter),
                Full: ConstantFormatter.DefaultOf(parameter, full: true)))
            .DistinctBy(value => value.Full)
            .ToList();
        return defaults.Count == 1 ? " " + defaults[0].Short : "";
    }

    /// <summary>
    /// The blocks for the callers whose calls the answer did not look at or counted
    /// differently, each a sentence with the sum and the members indented beneath it: those
    /// whose calls the decompiled C# does not show as a call, those that failed to decompile,
    /// and those whose decompiled C# shows more calls than their IL holds. The virtual calls
    /// through the slot of an overriding target, which are counted under the declaration that
    /// introduced the slot, stand in a block of their own that names the declaration, since
    /// only some of them reach the target. Every line starts with a line break.
    /// </summary>
    /// <remarks>
    /// The sum keeps a count of zero a proof of absence: it says how many calls the answer did
    /// not look at, also when <paramref name="limit"/> shows no member. The members are cut by
    /// the limit, for each block on its own, since a method can have such calls in many
    /// members: the constructor of one delegate type of a game assembly has them in 59.
    /// </remarks>
    private static string FormatShortfalls(IReadOnlyList<CallSiteFinder.Shortfall> shortfalls,
        Func<ISymbol, string> caller, int limit)
    {
        var text = new StringBuilder();
        var through = ReferenceFormatter.NamesFor(shortfalls.Select(shortfall => shortfall.Through).OfType<IMethod>());
        var blocks = shortfalls
            .GroupBy(shortfall => (
                Kind: shortfall.Failure != null ? Block.FailedToDecompile
                    : shortfall.Found < shortfall.Expected ? Block.NotShownAsCall : Block.ShowsMore,
                To: shortfall.Through is null ? "" : " to " + through(shortfall.Through)))
            .OrderBy(block => block.Key.Kind).ThenBy(block => block.Key.To, StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            // One line for a member, also when it calls several of the overloads asked about.
            var members = block
                .GroupBy(shortfall => caller(shortfall.Caller))
                .Select(member => (
                    Name: member.Key,
                    Expected: member.Sum(shortfall => shortfall.Expected),
                    Found: member.Sum(shortfall => shortfall.Found),
                    member.First().Failure))
                .ToList();
            var to = block.Key.To;
            var inMembers = $"in {Member.Count(members.Count)}";
            var (sentence, lines) = block.Key.Kind switch
            {
                Block.NotShownAsCall => (
                    $"Not shown: {Call.Count(members.Sum(member => member.Expected - member.Found))}{to} " +
                        $"{inMembers}, which the decompiled C# does not show as a call:",
                    members.Select(member => member.Name + Share(member.Expected - member.Found, member.Expected))),
                Block.FailedToDecompile => (
                    $"Not shown: {Call.Count(members.Sum(member => member.Expected))}{to} {inMembers}, " +
                        "which failed to decompile:",
                    members.Select(member => $"{member.Name}{Share(member.Expected, member.Expected)} ({member.Failure})")),
                _ => (members.Count == 1
                        ? $"Check: 1 member shows more calls{to} in the decompiled C# than its IL holds:"
                        : $"Check: {members.Count} members show more calls{to} in the decompiled C# than their IL holds:",
                    members.Select(member => $"{member.Name}: {member.Found} where the IL holds {member.Expected}")),
            };
            text.Append('\n').Append(sentence).Append(ListOutput.Beneath(lines.ToList(), limit));
        }
        return text.ToString();
    }

    /// <summary>What a block after the calls says of its members, in the order the blocks stand.</summary>
    private enum Block
    {
        NotShownAsCall,
        FailedToDecompile,
        ShowsMore,
    }

    /// <summary>
    /// ": 2 of 3" for a member <paramref name="part"/> of whose <paramref name="all"/> calls
    /// are meant, and nothing for its one call, which is the common case.
    /// </summary>
    private static string Share(int part, int all) => part == 1 && all == 1 ? "" : $": {part} of {all}";
}
