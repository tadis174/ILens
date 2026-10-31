using System.Text;
using System.Text.RegularExpressions;

namespace ILens;

/// <summary>
/// The lines that differ between the two bodies <c>compare</c> prints for one method, as a
/// unified diff: under the lines <c>--- A: path</c> and <c>+++ B: path</c>, a hunk for each run
/// of changes, headed <c>@@ -start,count +start,count @@</c>, with the unchanged lines around it,
/// and each line marked with a space, with <c>-</c> for a line of A's body that B's lacks or with
/// <c>+</c> for a line of B's that A's lacks.
/// </summary>
/// <remarks>
/// <para>C# is compared line by line as it is printed.</para>
/// <para>IL is compared without each instruction's own offset and with the offsets a branch, a
/// switch or an exception region names set aside: an instruction inserted early moves every later
/// offset and every jump over it, and a diff of the text as printed marked about two of three lines
/// of a game method that gained two instructions. A line that names offsets counts as unchanged
/// only where each offset leads, in A and in B, to lines the diff pairs, so a jump that lands
/// elsewhere shows although its text without offsets is the same. A removed line prints as A has
/// it and every other line as B has it, each with its own build's offsets. The code the compiler
/// generated for the method follows its own body, each part under a line
/// <c>// Generated: name</c>, and a hunk that starts inside such a part names it after its
/// <c>@@</c>, as git names the function a hunk lies in.</para>
/// <para>The edit script is Myers', which git uses by default, found in linear space by halving
/// each region at its middle snake, so it marks no more lines than a shortest edit script does.
/// A budget of steps bounds the search: past it, what is left of a region counts as replaced as a
/// whole, which keeps the diff correct and makes it about as long as the two bodies.</para>
/// </remarks>
public static class BodyDiff
{
    /// <summary>The unchanged lines shown around each change unless the caller asks for others.</summary>
    public const int DefaultContext = 3;

    private const string GeneratedPrefix = "// Generated: ";

    private enum Kind : byte { Same, Removed, Added }

    /// <summary>A line of the diff: one of A's (<see cref="A"/>), one of B's, or a pair.</summary>
    private readonly record struct Op(Kind Kind, int A, int B);

    /// <summary>
    /// The diff of two bodies as <c>compare</c> prints it, with <paramref name="context"/>
    /// unchanged lines around each change; a context larger than the bodies shows them whole.
    /// </summary>
    public static string Format(string a, string b, string pathA, string pathB, int context, bool il)
    {
        var aLines = Lines(a);
        var bLines = Lines(b);
        var ops = il ? IlOps(aLines, bLines) : Diff(aLines, bLines);
        if (ops.All(op => op.Kind == Kind.Same))
            // The caller has found the two texts unequal, and every difference between two texts
            // lies in some line: the C# is compared as printed, and the IL only without offsets,
            // which equal instructions put at equal places.
            throw new InvalidOperationException("The two bodies differ, but no line of them does.");

        var aParts = il ? PartHeads(aLines) : null;
        var bParts = il ? PartHeads(bLines) : null;
        var text = new StringBuilder();
        text.Append("--- A: ").Append(pathA).Append('\n');
        text.Append("+++ B: ").Append(pathB);
        // The lines of each side before the next hunk, counted once over the whole diff.
        int position = 0, aBefore = 0, bBefore = 0;
        foreach (var hunk in Hunks(ops, Math.Max(0, context)))
        {
            for (; position < hunk.Start; position++)
            {
                if (ops[position].Kind != Kind.Added) aBefore++;
                if (ops[position].Kind != Kind.Removed) bBefore++;
            }
            text.Append('\n');
            AppendHunk(text, ops, hunk, aBefore, bBefore, aLines, bLines, aParts, bParts);
        }
        return text.ToString();
    }

    /// <summary>
    /// A body's lines, without the carriage returns the decompiler and the disassembler end them
    /// with and without the empty line after the last one.
    /// </summary>
    private static string[] Lines(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd('\r');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>The diff of two lists of keys, a line of each, in the order of the lines.</summary>
    private static List<Op> Diff(string[] aKeys, string[] bKeys)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] Ids(string[] keys)
        {
            var result = new int[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                if (!ids.TryGetValue(keys[i], out var id))
                    ids.Add(keys[i], id = ids.Count);
                result[i] = id;
            }
            return result;
        }

        var a = Ids(aKeys);
        var b = Ids(bKeys);
        var differ = new Differ(a, b);
        differ.Run(0, a.Length, 0, b.Length);
        return Compact(differ.Ops, a, b);
    }

    /// <summary>
    /// Moves each run of changed lines as far down as lines equal to its first one allow, as GNU
    /// diff and git do, and puts the removed lines of each run before its added ones. Where a block
    /// was inserted after a line equal to its own last one, the edit script can place the block
    /// either side of that line, and the later place reads as the code was written: a condition
    /// added after an equal jump shows as a call and a jump added, not as a jump and a call. The
    /// unchanged lines of each side keep the same sequence, so they still pair in order.
    /// </summary>
    private static List<Op> Compact(List<Op> ops, int[] a, int[] b)
    {
        var aChanged = new bool[a.Length];
        var bChanged = new bool[b.Length];
        foreach (var op in ops)
        {
            if (op.Kind == Kind.Removed) aChanged[op.A] = true;
            else if (op.Kind == Kind.Added) bChanged[op.B] = true;
        }
        Slide(a, aChanged);
        Slide(b, bChanged);

        var result = new List<Op>(ops.Count);
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            while (i < a.Length && aChanged[i]) result.Add(new Op(Kind.Removed, i++, -1));
            while (j < b.Length && bChanged[j]) result.Add(new Op(Kind.Added, -1, j++));
            if (i < a.Length && j < b.Length)
                result.Add(new Op(Kind.Same, i++, j++));
            else if (i < a.Length || j < b.Length)
                throw new InvalidOperationException("The two sides of the diff keep unequal numbers of unchanged lines.");
        }
        return result;
    }

    private static void Slide(int[] keys, bool[] changed)
    {
        var start = 0;
        while (start < keys.Length)
        {
            if (!changed[start])
            {
                start++;
                continue;
            }
            var end = start;
            while (end < keys.Length && changed[end]) end++;
            while (end < keys.Length && keys[start] == keys[end])
            {
                changed[start++] = false;
                changed[end++] = true;
                // A run that reaches the next one goes on as one run.
                while (end < keys.Length && changed[end]) end++;
            }
            start = end;
        }
    }

    /// <summary>
    /// Puts the removed lines of each run of changes before its added lines, as a unified diff
    /// prints them; each side keeps its own order.
    /// </summary>
    private static List<Op> GroupChanges(List<Op> ops)
    {
        var result = new List<Op>(ops.Count);
        for (var i = 0; i < ops.Count;)
        {
            if (ops[i].Kind == Kind.Same)
            {
                result.Add(ops[i++]);
                continue;
            }
            var end = i;
            while (end < ops.Count && ops[end].Kind != Kind.Same) end++;
            result.AddRange(ops.Skip(i).Take(end - i).Where(op => op.Kind == Kind.Removed));
            result.AddRange(ops.Skip(i).Take(end - i).Where(op => op.Kind == Kind.Added));
            i = end;
        }
        return result;
    }

    /// <summary>
    /// Myers' difference algorithm over two sequences of line numbers, as Neil Fraser's
    /// diff-match-patch implements it: the common start and end are taken off, and what lies
    /// between is halved at the middle snake, the furthest-reaching paths from both ends meeting.
    /// </summary>
    private sealed class Differ(int[] a, int[] b)
    {
        /// <summary>
        /// The steps the search may take over a whole diff before it counts what is left of a
        /// region as replaced. Counted in steps rather than time, so a diff stays the same from
        /// one call to the next.
        /// </summary>
        /// <remarks>
        /// Measured on 2026-10-14 over the 8,952 diffs of changed methods, in C# and in IL, of a
        /// sample of real version pairs (a game's patches, mods, .NET runtime and SDK libraries):
        /// no search reached the budget, every C# diff marked the fewest lines possible, and the
        /// slowest diff took 0.3 s; a static constructor of 15,000 lines with 5,059 edits took
        /// 0.1 s, and with a tenth of this budget it was cut and marked 7,623 lines. Two bodies of
        /// 15,000 lines that share no line reach the budget after 0.5 s.
        /// </remarks>
        private const long Budget = 100_000_000;

        private long _steps;

        public List<Op> Ops { get; } = [];

        public void Run(int aStart, int aEnd, int bStart, int bEnd)
        {
            while (aStart < aEnd && bStart < bEnd && a[aStart] == b[bStart])
                Ops.Add(new Op(Kind.Same, aStart++, bStart++));
            var suffix = 0;
            while (aEnd - suffix > aStart && bEnd - suffix > bStart && a[aEnd - suffix - 1] == b[bEnd - suffix - 1])
                suffix++;
            aEnd -= suffix;
            bEnd -= suffix;

            if (aStart == aEnd || bStart == bEnd || !Bisect(aStart, aEnd, bStart, bEnd))
            {
                for (var i = aStart; i < aEnd; i++) Ops.Add(new Op(Kind.Removed, i, -1));
                for (var j = bStart; j < bEnd; j++) Ops.Add(new Op(Kind.Added, -1, j));
            }
            for (var i = 0; i < suffix; i++)
                Ops.Add(new Op(Kind.Same, aEnd + i, bEnd + i));
        }

        /// <summary>
        /// Finds the middle snake of a region whose ends differ and diffs the parts before and
        /// after it; false when the region shares nothing or the budget runs out first.
        /// </summary>
        private bool Bisect(int aStart, int aEnd, int bStart, int bEnd)
        {
            var n = aEnd - aStart;
            var m = bEnd - bStart;
            var maxD = (n + m + 1) / 2;
            var offset = maxD;
            var length = 2 * maxD + 2;
            var v1 = new int[length];
            var v2 = new int[length];
            Array.Fill(v1, -1);
            Array.Fill(v2, -1);
            v1[offset + 1] = 0;
            v2[offset + 1] = 0;
            var delta = n - m;
            // With an odd difference the forward path meets the reverse one; with an even one the
            // reverse path meets the forward one.
            var front = delta % 2 != 0;
            int k1Start = 0, k1End = 0, k2Start = 0, k2End = 0;
            for (var d = 0; d < maxD; d++)
            {
                if (_steps > Budget)
                    return false;

                for (var k1 = -d + k1Start; k1 <= d - k1End; k1 += 2)
                {
                    _steps++;
                    var k1Offset = offset + k1;
                    var x1 = k1 == -d || (k1 != d && v1[k1Offset - 1] < v1[k1Offset + 1])
                        ? v1[k1Offset + 1]
                        : v1[k1Offset - 1] + 1;
                    var y1 = x1 - k1;
                    while (x1 < n && y1 < m && a[aStart + x1] == b[bStart + y1])
                    {
                        x1++;
                        y1++;
                        _steps++;
                    }
                    v1[k1Offset] = x1;
                    if (x1 > n)
                        k1End += 2;
                    else if (y1 > m)
                        k1Start += 2;
                    else if (front)
                    {
                        var k2Offset = offset + delta - k1;
                        if (k2Offset >= 0 && k2Offset < length && v2[k2Offset] != -1 && x1 >= n - v2[k2Offset])
                        {
                            Run(aStart, aStart + x1, bStart, bStart + y1);
                            Run(aStart + x1, aEnd, bStart + y1, bEnd);
                            return true;
                        }
                    }
                }

                for (var k2 = -d + k2Start; k2 <= d - k2End; k2 += 2)
                {
                    _steps++;
                    var k2Offset = offset + k2;
                    var x2 = k2 == -d || (k2 != d && v2[k2Offset - 1] < v2[k2Offset + 1])
                        ? v2[k2Offset + 1]
                        : v2[k2Offset - 1] + 1;
                    var y2 = x2 - k2;
                    while (x2 < n && y2 < m && a[aEnd - x2 - 1] == b[bEnd - y2 - 1])
                    {
                        x2++;
                        y2++;
                        _steps++;
                    }
                    v2[k2Offset] = x2;
                    if (x2 > n)
                        k2End += 2;
                    else if (y2 > m)
                        k2Start += 2;
                    else if (!front)
                    {
                        var k1Offset = offset + delta - k2;
                        if (k1Offset >= 0 && k1Offset < length && v1[k1Offset] != -1)
                        {
                            var x1 = v1[k1Offset];
                            var y1 = offset + x1 - k1Offset;
                            if (x1 >= n - x2)
                            {
                                Run(aStart, aStart + x1, bStart, bStart + y1);
                                Run(aStart + x1, aEnd, bStart + y1, bEnd);
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// The diff of two IL texts, compared without the offsets their lines carry and name, with a
    /// line that names offsets kept as a pair only where they lead to paired lines.
    /// </summary>
    private static List<Op> IlOps(string[] aLines, string[] bLines)
    {
        var aRead = IlLine.ReadAll(aLines);
        var bRead = IlLine.ReadAll(bLines);
        var ops = Diff(aRead.Select(line => line.Key).ToArray(), bRead.Select(line => line.Key).ToArray());

        var aAt = LinesByOffset(aRead);
        var bAt = LinesByOffset(bRead);
        var paired = new Dictionary<int, int>();
        foreach (var op in ops)
            if (op.Kind == Kind.Same)
                paired[op.A] = op.B;

        // The line an offset leads to, or -1 for one that leads past the last instruction, as the
        // end of the last exception region does.
        static int Target(Dictionary<(int Part, int Offset), int> at, IlLine line, int offset) =>
            at.TryGetValue((line.Part, offset), out var index) ? index : -1;

        bool SameTargets(int aIndex, int bIndex)
        {
            var aLine = aRead[aIndex];
            var bLine = bRead[bIndex];
            for (var t = 0; t < aLine.Targets.Length; t++)
            {
                var aTarget = Target(aAt, aLine, aLine.Targets[t]);
                var bTarget = Target(bAt, bLine, bLine.Targets[t]);
                var same = aTarget < 0 || bTarget < 0
                    ? aTarget == bTarget
                    : paired.TryGetValue(aTarget, out var pairedTarget) && pairedTarget == bTarget;
                if (!same)
                    return false;
            }
            return true;
        }

        var result = new List<Op>(ops.Count);
        foreach (var op in ops)
        {
            if (op.Kind == Kind.Same && aRead[op.A].Targets.Length > 0 && !SameTargets(op.A, op.B))
            {
                result.Add(new Op(Kind.Removed, op.A, -1));
                result.Add(new Op(Kind.Added, -1, op.B));
            }
            else
                result.Add(op);
        }
        return GroupChanges(result);
    }

    /// <summary>
    /// The line each instruction offset of an IL text stands on. Each part of the text, the
    /// method's own body and each method generated for it, counts its offsets from zero.
    /// </summary>
    private static Dictionary<(int Part, int Offset), int> LinesByOffset(IlLine[] lines)
    {
        var at = new Dictionary<(int Part, int Offset), int>();
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Offset >= 0)
                at.TryAdd((lines[i].Part, lines[i].Offset), i);
        return at;
    }

    /// <summary>
    /// A line of an IL text as the diff reads it: its text without the offsets it carries or
    /// names, the part of the text it belongs to, its own offset, and the offsets it names.
    /// </summary>
    private sealed record IlLine(string Key, int Part, int Offset, int[] Targets)
    {
        // The disassembler writes an instruction as IL_<offset>: <opcode> <operand>, a branch's
        // operand as IL_<offset>, a switch's as (IL_<offset>, ...), and an exception region as
        // .try IL_<start>-IL_<end> <kind> IL_<start>-IL_<end>, where the kind is finally, fault,
        // catch with the caught type, or filter IL_<offset> handler with one space more.
        private static readonly Regex Instruction = new(@"^IL_([0-9a-f]+): (\S+)(?: (.*))?$", RegexOptions.CultureInvariant);
        private static readonly Regex Label = new(@"^IL_([0-9a-f]+)$", RegexOptions.CultureInvariant);
        private static readonly Regex Labels = new(@"^\((IL_[0-9a-f]+(?:, IL_[0-9a-f]+)*)?\)$", RegexOptions.CultureInvariant);
        private static readonly Regex Region = new(
            @"^\.try IL_([0-9a-f]+)-IL_([0-9a-f]+) (.*) IL_([0-9a-f]+)-IL_([0-9a-f]+)$", RegexOptions.CultureInvariant);
        private static readonly Regex Filter = new(@"^filter IL_([0-9a-f]+) handler ", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> Branches = new(StringComparer.Ordinal)
        {
            "br", "br.s", "brfalse", "brfalse.s", "brtrue", "brtrue.s", "beq", "beq.s", "bne.un", "bne.un.s",
            "bge", "bge.s", "bge.un", "bge.un.s", "bgt", "bgt.s", "bgt.un", "bgt.un.s",
            "ble", "ble.s", "ble.un", "ble.un.s", "blt", "blt.s", "blt.un", "blt.un.s", "leave", "leave.s",
        };

        /// <summary>
        /// The lines of an IL text, each with the part it belongs to: the method's own body is
        /// part 0, and each line <c>// Generated: name</c> starts the next.
        /// </summary>
        public static IlLine[] ReadAll(string[] lines)
        {
            var result = new IlLine[lines.Length];
            var part = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(GeneratedPrefix, StringComparison.Ordinal))
                    part++;
                result[i] = Read(lines[i], part);
            }
            return result;
        }

        private static IlLine Read(string line, int part)
        {
            var instruction = Instruction.Match(line);
            if (instruction.Success)
            {
                var offset = Hex(instruction.Groups[1].Value);
                var opcode = instruction.Groups[2].Value;
                var operand = instruction.Groups[3].Success ? instruction.Groups[3].Value : null;
                if (operand != null && Branches.Contains(opcode) && Label.Match(operand) is { Success: true } label)
                    return new IlLine(opcode + " IL_", part, offset, [Hex(label.Groups[1].Value)]);
                if (operand != null && opcode == "switch" && Labels.Match(operand) is { Success: true } labels)
                {
                    var targets = labels.Groups[1].Success
                        ? labels.Groups[1].Value.Split(", ").Select(target => Hex(target[3..])).ToArray()
                        : [];
                    return new IlLine("switch (" + string.Join(", ", targets.Select(_ => "IL_")) + ")", part, offset, targets);
                }
                return new IlLine(operand == null ? opcode : opcode + " " + operand, part, offset, []);
            }

            var region = Region.Match(line);
            if (region.Success)
            {
                var kind = region.Groups[3].Value;
                int[] filter = [];
                if (Filter.Match(kind) is { Success: true } filterMatch)
                {
                    filter = [Hex(filterMatch.Groups[1].Value)];
                    kind = "filter IL_ handler " + kind[filterMatch.Length..];
                }
                return new IlLine(
                    ".try IL_-IL_ " + kind + " IL_-IL_",
                    part,
                    -1,
                    [Hex(region.Groups[1].Value), Hex(region.Groups[2].Value), .. filter,
                        Hex(region.Groups[4].Value), Hex(region.Groups[5].Value)]);
            }

            return new IlLine(line, part, -1, []);
        }

        private static int Hex(string digits) => int.Parse(digits, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// For each line of an IL text, the line that heads the generated part it belongs to, or -1
    /// for a line of the method's own body.
    /// </summary>
    private static int[] PartHeads(string[] lines)
    {
        var heads = new int[lines.Length];
        var head = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(GeneratedPrefix, StringComparison.Ordinal))
                head = i;
            heads[i] = head;
        }
        return heads;
    }

    /// <summary>
    /// The ranges of the diff's lines that make a hunk each: every change with up to
    /// <paramref name="context"/> unchanged lines on either side, two changes in one hunk when
    /// the unchanged lines between them are no more than twice the context.
    /// </summary>
    private static List<(int Start, int End)> Hunks(List<Op> ops, int context)
    {
        context = Math.Min(context, ops.Count);
        var hunks = new List<(int Start, int End)>();
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == Kind.Same)
                continue;
            var start = Math.Max(0, i - context);
            var end = Math.Min(ops.Count, i + context + 1);
            if (hunks.Count > 0 && start <= hunks[^1].End)
                hunks[^1] = (hunks[^1].Start, Math.Max(hunks[^1].End, end));
            else
                hunks.Add((start, end));
        }
        return hunks;
    }

    private static void AppendHunk(StringBuilder text, List<Op> ops, (int Start, int End) hunk, int aBefore, int bBefore,
        string[] aLines, string[] bLines, int[] aParts, int[] bParts)
    {
        int aCount = 0, bCount = 0;
        for (var i = hunk.Start; i < hunk.End; i++)
        {
            if (ops[i].Kind != Kind.Added) aCount++;
            if (ops[i].Kind != Kind.Removed) bCount++;
        }

        // A unified diff writes a range of one line as its number alone, and an empty range by
        // the number of the line before it.
        static string Range(int before, int count) =>
            count == 1 ? $"{before + 1}" : count == 0 ? $"{before},0" : $"{before + 1},{count}";

        text.Append("@@ -").Append(Range(aBefore, aCount)).Append(" +").Append(Range(bBefore, bCount)).Append(" @@");

        // A hunk inside generated code names the part it lies in, unless the hunk shows the
        // part's own line.
        var first = ops[hunk.Start];
        var (lines, heads, index) = first.Kind == Kind.Removed ? (aLines, aParts, first.A) : (bLines, bParts, first.B);
        if (heads != null && heads[index] >= 0 && heads[index] < index)
            text.Append(' ').Append(lines[heads[index]]);

        for (var i = hunk.Start; i < hunk.End; i++)
        {
            var op = ops[i];
            text.Append('\n');
            switch (op.Kind)
            {
                case Kind.Same: text.Append(' ').Append(bLines[op.B]); break;
                case Kind.Removed: text.Append('-').Append(aLines[op.A]); break;
                default: text.Append('+').Append(bLines[op.B]); break;
            }
        }
    }
}
