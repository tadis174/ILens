using System.Reflection.Metadata;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace ILens;

/// <summary>
/// The methods whose code the IL reader cannot read to its end, and the line an answer that
/// read their code ends with. Such code breaks off inside an instruction, as a tool that guards
/// an assembly against decompiling writes it on purpose and a damaged file can have it. Every
/// pass over the method bodies reads it up to the break and answers from what it read, so a
/// use behind the break is missing from the answer, and the line names the methods that may
/// hold one. An assembly without such code answers without the line.
/// </summary>
internal static class BrokenBodies
{
    /// <summary>How many methods the line names before it counts the rest.</summary>
    public const int Named = 5;

    /// <summary>
    /// Every method of <paramref name="file"/> whose body the IL reader cannot read to its end:
    /// its header cannot be read, or an instruction runs past the end of the code, its opcode or
    /// its operand. The passes over the method bodies read such a body up to that instruction.
    /// </summary>
    public static IReadOnlySet<MethodDefinitionHandle> In(MetadataFile file)
    {
        var metadata = file.Metadata;
        var broken = new HashSet<MethodDefinitionHandle>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var definition = metadata.GetMethodDefinition(handle);
            if (definition.RelativeVirtualAddress == 0)
                continue;
            try
            {
                var reader = file.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
                while (reader.RemainingBytes > 0)
                {
                    if (!SkipInstruction(ref reader))
                    {
                        broken.Add(handle);
                        break;
                    }
                }
            }
            catch (BadImageFormatException)
            {
                broken.Add(handle);
            }
        }
        return broken;
    }

    /// <summary>
    /// Steps over one instruction, or answers false where its operand runs past the end of the
    /// code, where a pass that reads the operand, as one that reads the method a call names,
    /// stops with an exception.
    /// </summary>
    /// <remarks>
    /// Library behavior, read in ICSharpCode.Decompiler 11.2.0.10050: <c>ILParser.SkipOperand</c>
    /// moves to the end of the code without an exception where an operand runs past it, so the
    /// operand's size is checked here; <c>ILOpCodeExtensions.GetOperandType</c> answers 255 for an
    /// opcode it does not know, which <see cref="SizeOf"/> reads as one without an operand, as
    /// ILSpy's internal <c>ILParser.OperandSize</c> does. The cases: BrokenBodyTests.
    /// </remarks>
    private static bool SkipInstruction(ref BlobReader reader)
    {
        var type = reader.DecodeOpCode().GetOperandType();
        long size;
        if (type == OperandType.Switch)
        {
            if (reader.RemainingBytes < 4)
                return false;
            size = 4L * reader.ReadUInt32();
        }
        else
        {
            size = SizeOf(type);
        }
        if (size > reader.RemainingBytes)
            return false;
        reader.Offset += (int)size;
        return true;
    }

    /// <summary>
    /// The size of an operand of <paramref name="type"/>, other than a switch's, as ILSpy's
    /// internal <c>ILParser.OperandSize</c> gives it.
    /// </summary>
    private static int SizeOf(OperandType type) => type switch
    {
        OperandType.I8 or OperandType.R => 8,
        OperandType.BrTarget or OperandType.Field or OperandType.I or OperandType.Method or OperandType.Sig
            or OperandType.String or OperandType.Tok or OperandType.Type or OperandType.ShortR => 4,
        OperandType.Variable => 2,
        OperandType.ShortBrTarget or OperandType.ShortI or OperandType.ShortVariable => 1,
        _ => 0,
    };

    /// <summary>
    /// The line for <paramref name="names"/>, the methods of an answer's scope whose code breaks
    /// off, each as the answer names it, distinct and sorted, or null for none. One method is
    /// named alone; of more, the first <see cref="Named"/> are named and the rest counted. The
    /// noun is "members" where <paramref name="methodsOnly"/> is false, as for a property named
    /// for its accessor. <paramref name="word"/> begins the line, as the answer's other lines of
    /// what it did not read begin.
    /// </summary>
    public static string Line(IReadOnlyList<string> names, bool methodsOnly, string word = "Not checked")
    {
        const string where = "past where it breaks off inside an instruction";
        if (names.Count == 0)
            return null;
        if (names.Count == 1)
            return $"{word}: the code of {names[0]} {where}";
        var more = names.Count - Named;
        return $"{word}: the code of {names.Count} {(methodsOnly ? "methods" : "members")} {where}: " +
            string.Join(", ", names.Take(Named)) + (more > 0 ? $" and {more} more" : "");
    }
}
