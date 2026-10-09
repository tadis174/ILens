using System.Text;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// analyze's answer for kind call_sites: every call to a method, with what each call passes.
/// </summary>
public static class CallSiteReport
{
    private static readonly Noun Call = Noun.Regular("call");

    /// <summary>
    /// Every call to the overloads of <paramref name="methodName"/> on <paramref name="type"/>,
    /// or to the one a hint selects. With <paramref name="parameterName"/>, each line shows only
    /// the argument for that parameter, and <paramref name="nonDefaultOnly"/> keeps the calls
    /// whose argument is not provably the parameter's default.
    /// </summary>
    public static string Format(AssemblyHost host, ITypeDefinition type, string methodName,
        int? parameterCount, string[] parameterTypes, string parameterName, bool nonDefaultOnly, int limit)
    {
        var byParameter = !string.IsNullOrEmpty(parameterName);
        if (nonDefaultOnly && !byParameter)
            throw new ArgumentException(
                $"{ParameterNames.NonDefaultOnly} needs {ParameterNames.ParameterName}: it compares the argument " +
                "passed for that parameter with the parameter's default.");

        var targets = host.Resolver.ResolveMethodOverloads(type, methodName, parameterCount, parameterTypes);
        if (byParameter)
            targets = OverloadsWith(targets, parameterName, nonDefaultOnly, type, methodName);

        var subject = targets.Count == 1
            ? ReferenceFormatter.FormatMethodReference(targets[0])
            : $"{ReferenceFormatter.FormatTypeName(type)}.{methodName} ({targets.Count} overloads)";
        var result = CallSiteFinder.Find(host, targets);
        var routes = string.Concat(targets
            .Select(target => ReferenceFormatter.FormatExplicitRoute(target)
                ?? ReferenceFormatter.FormatOverrideRoute(target, listsCalls: true))
            .Where(route => route != null)
            .Distinct()
            .Select(route => "\n" + route));
        if (result.Calls.Count == 0 && result.Shortfalls.Count == 0)
            return ListOutput.Header(0, Call, "to " + subject) + routes;

        var calls = result.Calls;
        string header;
        if (nonDefaultOnly)
        {
            calls = calls.Where(call => !ArgumentFor(call, parameterName).IsDefault).ToList();
            header = $"{calls.Count} of {Call.Count(result.Calls.Count)} to {subject} " +
                $"{(calls.Count == 1 ? "passes" : "pass")} {parameterName} other than its " +
                $"default{SharedDefault(targets, parameterName)}:";
        }
        else if (byParameter)
        {
            header = $"{Call.Count(calls.Count)} to {subject}, argument for {parameterName}:";
        }
        else
        {
            header = $"{Call.Count(calls.Count)} to {subject}:";
        }

        var sb = new StringBuilder(header);
        foreach (var call in calls.Take(limit))
        {
            sb.Append('\n').Append(ReferenceFormatter.FormatSymbol(call.Caller)).Append(": ");
            if (!byParameter)
            {
                sb.Append(call.Call);
                continue;
            }
            var argument = ArgumentFor(call, parameterName);
            sb.Append(argument.Text);
            if (argument.IsDefault && !nonDefaultOnly)
                sb.Append(" (default)");
        }
        if (calls.Count > limit)
            sb.Append('\n').Append(ListOutput.Truncation(calls.Count - limit));

        // Shortfalls are never capped: each one is a call the answer above did not look at.
        foreach (var shortfall in result.Shortfalls)
            sb.Append('\n').Append(FormatShortfall(shortfall));
        sb.Append(routes);

        return sb.ToString();
    }

    /// <summary>
    /// The overloads that declare <paramref name="parameterName"/>. Throws when none does, and,
    /// for <paramref name="nonDefaultOnly"/>, when one declares it without a default, since
    /// there would be nothing to compare its arguments with.
    /// </summary>
    private static IReadOnlyList<IMethod> OverloadsWith(IReadOnlyList<IMethod> overloads,
        string parameterName, bool nonDefaultOnly, ITypeDefinition type, string methodName)
    {
        var withParameter = overloads
            .Where(method => method.Parameters.Any(p => p.Name == parameterName))
            .ToList();
        if (withParameter.Count == 0)
        {
            var names = overloads.SelectMany(method => method.Parameters)
                .Select(p => p.Name).Distinct();
            throw new ArgumentException(
                $"No overload of '{methodName}' on {ReferenceFormatter.FormatTypeName(type)} has a parameter named " +
                $"'{parameterName}'. Its parameters: {string.Join(", ", names)}.");
        }

        if (nonDefaultOnly)
        {
            var required = withParameter.FirstOrDefault(method =>
                !method.Parameters.First(p => p.Name == parameterName).IsOptional);
            if (required != null)
                throw new ArgumentException(
                    $"'{parameterName}' has no default value in " +
                    $"{ReferenceFormatter.FormatMethodReference(required)}, so {ParameterNames.NonDefaultOnly} has " +
                    "nothing to compare its arguments with.");
        }
        return withParameter;
    }

    private static CallSiteFinder.Argument ArgumentFor(CallSiteFinder.CallSite call, string parameterName) =>
        call.Arguments.First(argument => argument.Parameter.Name == parameterName);

    /// <summary>
    /// " 0" when every overload gives the parameter the same default, so the header can name
    /// it; empty otherwise, since no single value would be true for all of them.
    /// </summary>
    private static string SharedDefault(IReadOnlyList<IMethod> targets, string parameterName)
    {
        var defaults = targets
            .Select(method => method.Parameters.First(p => p.Name == parameterName).GetConstantValue())
            .Distinct()
            .ToList();
        return defaults.Count == 1 ? " " + CallSiteFinder.FormatConstant(defaults[0]) : "";
    }

    /// <summary>
    /// The line for calls the answer did not look at. Calls to the declaration an overriding
    /// target overrides name that declaration, since only some of them reach the target.
    /// </summary>
    private static string FormatShortfall(CallSiteFinder.Shortfall shortfall)
    {
        var caller = ReferenceFormatter.FormatSymbol(shortfall.Caller);
        var to = shortfall.Through is null ? "" : " to " + ReferenceFormatter.FormatMethodReference(shortfall.Through);
        if (shortfall.Failure != null)
            return $"Not shown: {Call.Count(shortfall.Expected)}{to} in {caller}, which failed to " +
                $"decompile ({shortfall.Failure})";
        if (shortfall.Found < shortfall.Expected)
            return $"Not shown: {shortfall.Expected - shortfall.Found} of " +
                $"{Call.Count(shortfall.Expected)}{to} in {caller}, which the decompiled C# does " +
                "not show as a call";
        return $"Check: {caller} shows {Call.Count(shortfall.Found)}{to} in the decompiled C# " +
            $"where its IL holds {shortfall.Expected}";
    }
}
