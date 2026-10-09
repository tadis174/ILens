using System.Text.RegularExpressions;

namespace ILens;

/// <summary>
/// Replaces the numbers a compiler gives generated names by position, so that a method whose
/// code did not change compares equal after a method added before it shifted them. Roslyn
/// numbers the code it generates for a lambda, a local function, an iterator or an async method
/// by the position of the method it is written in; it numbers a few other names in order of use
/// within a type or within the assembly; and Burst numbers its wrappers across the assembly.
/// </summary>
/// <remarks>
/// Only these known counters are replaced. Every other part of a name stays, among them the
/// numbers that carry meaning, such as a generic arity, and the hash a static data field is
/// named by. So an unknown numbering scheme makes an unchanged method show as changed, never a
/// change disappear. Each counter is replaced by its order of first appearance in the text, per
/// kind of counter. A bare placeholder would merge two names that differ only in their number,
/// such as two base-call helpers one method uses.
/// </remarks>
public static class GeneratedNameNormalizer
{
    // One alternative per counter, grouped by the sequence the compiler draws it from:
    //   method:            <M>b__N_K lambda, <M>g__F|N_K local function, <M>d__N state machine,
    //                      <>c__DisplayClassN_K closure class, <>9__N_K cached lambda, <>c__N
    //                      lambda class of a generic method, <M>O__N_K method-group cache of a
    //                      generic method. A lambda or local function inside a closure class,
    //                      <M>b__K or <M>g__F|K, carries no method number.
    //   base:              <>n__N, which calls a base method from a lambda or state machine.
    //   cache:             <N>__M, a method-group cache field of the class <>O.
    //   anonymousType:     <>f__AnonymousTypeN, numbered per assembly.
    //   anonymousDelegate: <>f__AnonymousDelegateN, numbered per assembly.
    //   burst:             the eight hexadecimal digits before $BurstDirectCall or
    //                      $PostfixBurstDelegate, numbered per assembly.
    private static readonly Regex Counters = new(
        @"(?<method>(?<=>b__)\d+(?=_\d)|(?<=>g__[^|'<>\s]*\|)\d+(?=_\d)|(?<=>d__)\d+" +
        @"|(?<=<>c__DisplayClass)\d+(?=_\d)|(?<=<>9__)\d+(?=_\d)|(?<=<>c__)\d+|(?<=>O__)\d+(?=_\d))" +
        @"|(?<base>(?<=<>n__)\d+)" +
        @"|(?<cache>(?<=<)\d+(?=>__))" +
        @"|(?<anonymousType>(?<=<>f__AnonymousType)\d+)" +
        @"|(?<anonymousDelegate>(?<=<>f__AnonymousDelegate)\d+)" +
        @"|(?<burst>(?<=_)[0-9A-F]{8}(?=\$(?:BurstDirectCall|PostfixBurstDelegate)))",
        RegexOptions.Compiled);

    private static readonly string[] Kinds = ["method", "base", "cache", "anonymousType", "anonymousDelegate", "burst"];

    /// <summary>
    /// <paramref name="text"/> with each known counter replaced by <c>#</c> and its order of
    /// first appearance among the counters of its kind, starting at 1.
    /// </summary>
    public static string Normalize(string text)
    {
        var numbers = new Dictionary<(string Kind, string Value), int>();
        var counts = new Dictionary<string, int>();
        return Counters.Replace(text, match =>
        {
            var kind = Kinds.First(kind => match.Groups[kind].Success);
            if (!numbers.TryGetValue((kind, match.Value), out var number))
            {
                number = counts.GetValueOrDefault(kind) + 1;
                counts[kind] = number;
                numbers[(kind, match.Value)] = number;
            }
            return "#" + number;
        });
    }
}
