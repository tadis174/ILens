using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for <c>analyze</c> on a field or property of an attribute class that
/// attributes set by name, as <c>Text</c> in <c>[Note(Text = "x")]</c>. Such an attribute writes
/// the member without any instruction and names it by its name alone, so who writes or uses the
/// member is the code that does and what carries such an attribute, and which class declares
/// the member follows from the attribute's class. The ExternalUser fixture's SetByName.cs sets
/// members of attribute classes of its own and of ExternalLib's TraitAttribute, through those
/// classes and through classes derived from them. ExternalLib.dll lands only in the References
/// folder of the test output, so the shared server cannot load it while the second server can.
/// </summary>
public sealed class NamedArgumentTests
    : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private const string N = "ExternalUser.SetByName";
    private const string Note = $"{N}.NoteAttribute";
    private const string Trait = "ExternalLib.TraitAttribute";
    private const string Library = "ILens could not load ExternalLib, Version=1.0.0.0";

    // What carries an attribute that sets NoteAttribute.Text by name, on each thing an attribute
    // can sit on, the method a lambda with the attribute is written in, and through a derived
    // class that inherits the property; among them the code that sets it.
    private const string TextWriters =
        $"{N}.Carriers\n{N}.Carriers.Field\n{N}.Carriers.Method()\n{N}.Carriers.OnLambda()\n" +
        $"{N}.Carriers.Parameter(int)\n{N}.Carriers.Property\n{N}.Carriers.Return()\n" +
        $"{N}.InCode.Make()\n{N}.Inherited\nassembly ExternalUser";

    private readonly ILensServerFixture _shared;
    private readonly ReferencesServerFixture _references;
    private readonly string _userPath;

    public NamedArgumentTests(ILensServerFixture shared, ReferencesServerFixture references)
    {
        _shared = shared;
        _references = references;
        _userPath = Path.Combine(Path.GetDirectoryName(shared.AssemblyPath)!, "ExternalUser.dll");
    }

    // A type, one of its members, the kind asked, and the whole answer. Every class here is one
    // the fixture defines.
    public static TheoryData<string, string, string, string> OwnMembers => new()
    {
        // An unrelated class's Text is another member.
        { Note, "Text", "assigned_by", $"10 writers of {Note}.Text [declared] (via set_Text):\n{TextWriters}" },
        {
            Note, "Text", "used_by",
            $"11 users of {Note}.Text [declared] (via get_Text, set_Text):\n" +
            TextWriters.Replace($"{N}.InCode.Make()\n", $"{N}.InCode.Make()\n{N}.InCode.Read(NoteAttribute)\n")
        },
        // An attribute can only set.
        { Note, "Text", "read_by", $"1 reader of {Note}.Text [declared] (via get_Text):\n{N}.InCode.Read(NoteAttribute)" },
        // Asked for through a class that inherits it, the property is the same one.
        {
            $"{N}.SideNoteAttribute", "Text", "assigned_by",
            $"10 writers of {Note}.Text [inherited from {Note}] (via set_Text):\n{TextWriters}"
        },
        { $"{N}.RemarkAttribute", "Text", "assigned_by", $"1 writer of {N}.RemarkAttribute.Text [declared] (via set_Text):\n{N}.Unrelated" },
        // A class that declares the name anew has a member of its own, which an attribute of
        // that class sets.
        { Note, "Title", "assigned_by", $"1 writer of {Note}.Title [declared] (via set_Title):\n{N}.Carriers" },
        { $"{N}.OwnNoteAttribute", "Title", "assigned_by", $"1 writer of {N}.OwnNoteAttribute.Title [declared] (via set_Title):\n{N}.Redeclared" },
        { Note, "Unset", "assigned_by", $"No writers of {Note}.Unset [declared] (via set_Unset)" },
        // A field, beside the constructor and the method that write it in code.
        {
            Note, "Weight", "assigned_by",
            $"5 writers of {Note}.Weight [declared]:\n" +
            $"{N}.Carriers.Field\n{N}.InCode.Make()\n{N}.Inherited\n{Note}..ctor(int)\nmodule ExternalUser.dll"
        },
        { Note, "Weight", "read_by", $"No readers of {Note}.Weight [declared]" },
        // The derived class's private field of the same name is not what its attribute sets:
        // C# passes over a member the place of the attribute cannot reach.
        {
            $"{N}.SideNoteAttribute", "Weight", "assigned_by",
            $"1 writer of {N}.SideNoteAttribute.Weight [declared]:\n{N}.SideNoteAttribute..ctor()"
        },
        // C# binds a name to the declaration that introduced it, so an attribute of the class
        // that overrides Order sets NoteAttribute's.
        { Note, "Order", "assigned_by", $"1 writer of {Note}.Order [declared] (via set_Order):\n{N}.Overridden" },
        {
            $"{N}.OrderedNoteAttribute", "Order", "assigned_by",
            $"No writers of {N}.OrderedNoteAttribute.Order [declared] (via set_Order)\n" +
            $"Override: code can call {Note}.Order instead, so only references to this override are listed; " +
            $"'call_sites' on get_Order and set_Order lists those calls whose receiver's type is " +
            $"{N}.OrderedNoteAttribute or derives from it."
        },
        // The setter asked for itself.
        { Note, "set_Text", "used_by", $"10 users of {Note}.set_Text(string) [declared]:\n{TextWriters}" },
        // The list of calls keeps to calls and counts the attributes, not what carries them.
        {
            Note, "set_Text", "call_sites",
            $"1 call to {Note}.set_Text(string):\n{N}.InCode.Make()\n  Text = \"code\"\n" +
            "Not shown: 9 uses as an attribute; used_by lists them"
        },
        { Note, "set_Unset", "call_sites", $"No calls to {Note}.set_Unset(string)" },
        // The property itself carries the attribute that sets it.
        { $"{N}.SelfAttribute", "Name", "assigned_by", $"1 writer of {N}.SelfAttribute.Name [declared] (via set_Name):\n{N}.SelfAttribute.Name" },
        // An attribute class with a type parameter.
        {
            $"{N}.TypedNoteAttribute<>", "Value", "assigned_by",
            $"1 writer of {N}.TypedNoteAttribute<>.Value [declared] (via set_Value):\n{N}.Typed"
        },
        // One attribute's bytes can be read two ways, only one of which sets Level.
        {
            $"{N}.SizedAttribute", "Level", "assigned_by",
            $"1 writer of {N}.SizedAttribute.Level [declared] (via set_Level):\n{N}.LevelSet\n" +
            $"Not checked: whether {N}.SizedAttribute on {N}.LevelSetOrNot sets it; " +
            "its bytes fit more than one size of ExternalLib.Pitch"
        },
        {
            $"{N}.SizedAttribute", "set_Level", "used_by",
            $"1 user of {N}.SizedAttribute.set_Level(int) [declared]:\n{N}.LevelSet\n" +
            $"Not checked: whether {N}.SizedAttribute on {N}.LevelSetOrNot sets it; " +
            "its bytes fit more than one size of ExternalLib.Pitch"
        },
    };

    [Theory]
    [MemberData(nameof(OwnMembers))]
    public async Task Analyze_OnAMemberAttributesSetByName_ListsWhatCarriesThem(
        string typeName, string memberName, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(_shared.Client, typeName, memberName, kind));
    }

    // A class of ExternalLib, a member, the kind asked, and the whole answer of the shared
    // server, which cannot load ExternalLib: whether another class of it inherits the name is
    // not known there.
    public static TheoryData<string, string, string, string> LibraryMembers => new()
    {
        // The class itself and a class of the fixture derived from it, beside the code that
        // sets the property. The fixture's class that declares Kind anew sets its own.
        {
            Trait, "Kind", "assigned_by",
            $"3 writers of {Trait}.Kind (via set_Kind):\n{N}.Direct\n{N}.ThroughLocalClass\n{N}.TraitInCode.Make()\n" +
            $"Not checked: whether ExternalLib.OwnTraitAttribute, ExternalLib.SubTraitAttribute inherit Kind from {Trait}; {Library}"
        },
        {
            Trait, "Kind", "used_by",
            $"3 users of {Trait}.Kind (via set_Kind):\n{N}.Direct\n{N}.ThroughLocalClass\n{N}.TraitInCode.Make()\n" +
            $"Not checked: whether ExternalLib.OwnTraitAttribute, ExternalLib.SubTraitAttribute inherit Kind from {Trait}; {Library}"
        },
        // Members no reference row names: a property and its setter, and a field.
        { Trait, "Caption", "assigned_by", $"1 writer of {Trait}.Caption:\n{N}.Captioned" },
        { Trait, "Caption", "read_by", $"No readers of {Trait}.Caption" },
        { Trait, "set_Caption", "used_by", $"1 user of {Trait}.set_Caption:\n{N}.Captioned" },
        { Trait, "Rank", "assigned_by", $"1 writer of {Trait}.Rank:\n{N}.Direct" },
        { Trait, "Rank", "read_by", $"No readers of {Trait}.Rank" },
        // The line that names what the assembly references names those members too.
        {
            Trait, "Unset", "assigned_by",
            $"No writers of {Trait}.Unset\n" +
            $"This assembly references no member Unset of {Trait}. It references: .ctor, Caption, Kind, Rank.\n" +
            "ILens cannot tell whether Unset is a constant, whose value the compiler copies into each use: " +
            $"no assembly it found defines {Trait}, which comes from ExternalLib, Version=1.0.0.0."
        },
        // Asked for on a derived class, the name is taken as named on that class.
        {
            "ExternalLib.SubTraitAttribute", "Kind", "assigned_by",
            $"1 writer of ExternalLib.SubTraitAttribute.Kind:\n{N}.ThroughLibraryClass\n" +
            $"Not checked: whether ExternalLib.OwnTraitAttribute, {Trait}, {N}.LocalTraitAttribute inherit Kind " +
            $"from ExternalLib.SubTraitAttribute; {Library}"
        },
        // Without the library nothing shows that Plaque is no attribute class, so an attribute
        // class of it that sets a member of the same name leaves the question open.
        {
            "ExternalLib.Plaque", "Caption", "assigned_by",
            $"1 writer of ExternalLib.Plaque.Caption (via set_Caption):\n{N}.TraitInCode.MakePlaque()\n" +
            $"Not checked: whether {Trait} inherits Caption from ExternalLib.Plaque; {Library}"
        },
    };

    [Theory]
    [MemberData(nameof(LibraryMembers))]
    public async Task Analyze_OnAMemberOfAnotherAssemblysAttribute_ListsWhatCarriesIt(
        string typeName, string memberName, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(_shared.Client, typeName, memberName, kind));
    }

    // The same questions of the server that loads ExternalLib, which reads there that
    // SubTraitAttribute inherits Kind and OwnTraitAttribute declares its own.
    [Theory]
    [InlineData(Trait, "Kind", "assigned_by",
        $"4 writers of {Trait}.Kind (via set_Kind):\n" +
        $"{N}.Direct\n{N}.ThroughLibraryClass\n{N}.ThroughLocalClass\n{N}.TraitInCode.Make()")]
    [InlineData("ExternalLib.SubTraitAttribute", "Kind", "assigned_by",
        $"1 writer of ExternalLib.SubTraitAttribute.Kind:\n{N}.ThroughLibraryClass")]
    [InlineData("ExternalLib.OwnTraitAttribute", "Kind", "assigned_by",
        $"1 writer of ExternalLib.OwnTraitAttribute.Kind:\n{N}.LibraryRedeclared")]
    [InlineData(Trait, "Unset", "assigned_by",
        $"No writers of {Trait}.Unset\n" +
        $"This assembly references no member Unset of {Trait}. It references: .ctor, Caption, Kind, Rank.")]
    [InlineData("ExternalLib.Plaque", "Caption", "assigned_by",
        $"1 writer of ExternalLib.Plaque.Caption (via set_Caption):\n{N}.TraitInCode.MakePlaque()")]
    public async Task Analyze_WhereTheAttributesLibraryLoads_ReadsWhichClassDeclaresTheMember(
        string typeName, string memberName, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(_references.Client, typeName, memberName, kind));
    }

    // A field takes the read and assign questions alone, also one known from attributes only.
    [Fact]
    public async Task UsedBy_OnAFieldOnlyAttributesSet_IsRefusedAsForAnyField()
    {
        CallToolResult result = await _shared.Client.CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["assembly"] = _userPath,
            ["type_name"] = Trait,
            ["member_name"] = "Rank",
            ["kind"] = "used_by",
        });

        Assert.True(result.IsError == true);
        Assert.Equal(
            "Error: ArgumentException: Analysis kind 'used_by' is not valid for Field. " +
            "Valid kinds for Field: read_by, assigned_by.",
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
    }

    // No reference row gives the setter's parameters, so a hint has nothing to select by, and
    // answering that the setter is not referenced would read as nothing using it.
    [Fact]
    public async Task AHint_OnASetterOnlyAttributesUse_IsRefused()
    {
        CallToolResult result = await _shared.Client.CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["assembly"] = _userPath,
            ["type_name"] = Trait,
            ["member_name"] = "set_Caption",
            ["kind"] = "used_by",
            ["parameter_count"] = 1,
        });

        Assert.True(result.IsError == true);
        Assert.Equal(
            $"Error: ArgumentException: 'set_Caption' on {Trait} is known here only from the attributes that set " +
            "Caption by name, which hold no parameter list to select by; omit parameter_count, parameter_types and " +
            "return_type.",
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
    }

    private async Task<string> Analyze(McpClient client, string typeName, string memberName, string kind)
    {
        CallToolResult result = await client.CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["assembly"] = _userPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        });
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        Assert.True(result.IsError != true, $"analyze returned an error: {text}");
        return text;
    }
}
