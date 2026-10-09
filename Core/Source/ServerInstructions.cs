namespace ILens;

/// <summary>
/// The instructions the server sends when a client connects, which a client may add to the
/// model's context. Claude Code loads them at session start, before any tool description, so
/// they say what ILens is for, which assemblies it can read and which tool answers what. Text
/// that changes with an ILens version belongs here and in the tool descriptions, never in a block
/// users paste into their projects, so an upgrade needs no edit outside the server.
/// </summary>
public static class ServerInstructions
{
    /// <summary>
    /// The length at which Claude Code cuts a server's instructions by default. The directory list
    /// is shortened to keep the whole text within it, so a cut never falls on the tools.
    /// </summary>
    public const int MaxLength = 2048;

    private const string Purpose =
        "ILens reads compiled .NET assemblies: it finds types and members, decompiles one type or member to C#, " +
        "analyzes who uses, calls, overrides or implements a symbol, compares two builds of an assembly, and " +
        "lists what a Harmony mod patches. Each answer covers only the question asked, so it costs far less than " +
        "dumping a file with ildasm or ilspycmd, and unlike source found on the web it matches the build inspected.";

    private const string RootsLead = "Assemblies under these directories can be inspected: ";

    private const string Tools =
        "- find_types: types by namespace or name substring.\n" +
        "- find_methods: methods by name, parameter types or return type, or one type's methods.\n" +
        "- list_members: a type's members without bodies.\n" +
        "- decompile: one member as C#, or a whole type.\n" +
        "- analyze: who uses, reads, writes, instantiates, overrides or implements a type or member, what a " +
        "member uses, and every call to a method with its arguments (kind call_sites).\n" +
        "- compare: two builds of an assembly, from the changed types down to one method's two bodies.\n" +
        "- find_harmony_dependencies: the members a Harmony mod patches and the fields it reaches by reflection.\n" +
        "- list_allowed_roots: the directories above.";

    private const string Usage =
        "Reading a type costs least through list_members, then decompile with member_name; a whole type is the " +
        "most expensive call. Names are written as ILens prints them, so a name from one answer can be passed to " +
        "the next tool.";

    /// <summary>
    /// The instructions for a server reading <paramref name="roots"/>. The directories follow the
    /// first paragraph, since they tell the model which assemblies ILens can answer for. When the
    /// whole text would exceed <see cref="MaxLength"/>, the list stops at the last directory that
    /// fits and counts the others, which list_allowed_roots lists in full.
    /// </summary>
    public static string For(IReadOnlyList<string> roots)
    {
        string Compose(string rootsLine) => $"{Purpose}\n\n{rootsLine}\n\n{Tools}\n\n{Usage}";

        if (roots.Count == 0)
            return Compose("No directories are configured, so no assembly can be loaded.");

        for (var shown = roots.Count; shown > 0; shown--)
        {
            var others = roots.Count - shown;
            var listed = string.Join("; ", roots.Take(shown));
            var text = Compose(others == 0
                ? RootsLead + listed
                : $"{RootsLead}{listed}; and {others} more, which list_allowed_roots lists");
            if (text.Length <= MaxLength)
                return text;
        }
        return Compose($"Assemblies under {roots.Count} directories can be inspected; list_allowed_roots lists them.");
    }
}
