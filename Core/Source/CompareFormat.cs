namespace ILens;

/// <summary>How the compare tool prints the two bodies of a method.</summary>
public enum CompareFormat
{
    /// <summary>Decompiled C#.</summary>
    Csharp,

    /// <summary>The normalized IL disassembly the comparison itself decides equality by.</summary>
    Il,
}
