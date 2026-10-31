using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for <c>analyze</c> with <c>implemented_by</c> on a member of an interface,
/// and for its agreement with <c>implements</c>: each lists what a class binds, read from the
/// other side. The ExplicitMembers fixture's Inherited.cs holds classes that bind an
/// interface's members to those of a base class without the interface, and its Sources.cs one
/// class for each way a member can have an interface member's name and parameters without
/// implementing it. The ExternalUser fixture's Probe inherits what it binds from a class of
/// ExternalLib, which only the second server can load. The forms that C# cannot write, a
/// member that implements under another name, accessor methods without a property and a class
/// whose rows name an interface without its base interface, are asked of an assembly written
/// through <see cref="RowImplementationAssembly"/>.
/// </summary>
public sealed class ImplementedByTests
    : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private const string Members = "ExplicitMembers.";
    private const string Inherited = "ExplicitMembers.Inherited.";
    private const string Reading = "ExternalUser.Inherited.IReading";

    private readonly ILensServerFixture _shared;
    private readonly ReferencesServerFixture _references;
    private readonly string _explicitMembersPath;
    private readonly string _userPath;
    private readonly string _callShapesPath;
    private readonly string _interfaceShapesPath;

    public ImplementedByTests(ILensServerFixture shared, ReferencesServerFixture references)
    {
        _shared = shared;
        _references = references;
        string testOutput = Path.GetDirectoryName(shared.AssemblyPath)!;
        _explicitMembersPath = Path.Combine(testOutput, "ExplicitMembers.dll");
        _userPath = Path.Combine(testOutput, "ExternalUser.dll");
        _callShapesPath = Path.Combine(testOutput, "CallShapes.dll");
        _interfaceShapesPath = Path.Combine(testOutput, "InterfaceShapes.dll");
    }

    /// <summary>
    /// A member that implements: its type, the name it is asked by, the line it prints as, and
    /// the classes its line ends with when only classes below its own bind it.
    /// </summary>
    private sealed record Implementer(string Type, string AskedAs, string Line, string BoundBy = "");

    /// <summary>A member of an interface, by the name it is asked by, with what implements it.</summary>
    private sealed record Binding(string Interface, string AskedAs, string Line, Implementer[] Implementers);

    private static readonly Dictionary<string, Binding> Bindings = new()
    {
        // Lister and LateLister inherit Get from PlainBase, which has no interface. Relister
        // lists IPart again below a Get that hides Holder's, where the compiler adds a method
        // to Relister that implements IPart.Get by its row.
        ["IPart.Get"] = new(Inherited + "IPart", "Get", Inherited + "IPart.Get()",
        [
            new(Inherited + "Holder", "Get", Inherited + "Holder.Get()"),
            new(Inherited + "PlainBase", "Get", Inherited + "PlainBase.Get()", $" (for {Inherited}LateLister, {Inherited}Lister)"),
            new(Inherited + "Relister", Inherited + "IPart.Get", Inherited + "Relister." + Inherited + "IPart.Get()"),
        ]),
        // ListerChild overrides the property Lister binds, so its Name runs for it. LateLister
        // finds Middle's override before PlainBase's property. Renamer implements explicitly.
        ["IPart.Name"] = new(Inherited + "IPart", "Name", Inherited + "IPart.Name",
        [
            new(Inherited + "Holder", "Name", Inherited + "Holder.Name"),
            new(Inherited + "ListerChild", "Name", Inherited + "ListerChild.Name"),
            new(Inherited + "Middle", "Name", Inherited + "Middle.Name", $" (for {Inherited}LateLister)"),
            new(Inherited + "PlainBase", "Name", Inherited + "PlainBase.Name", $" (for {Inherited}Lister)"),
            new(Inherited + "Renamer", Inherited + "IPart.Name", Inherited + "Renamer." + Inherited + "IPart.Name"),
        ]),
        // An accessor is implemented by the accessors of the same members.
        ["IPart.get_Name"] = new(Inherited + "IPart", "get_Name", Inherited + "IPart.get_Name()",
        [
            new(Inherited + "Holder", "get_Name", Inherited + "Holder.get_Name()"),
            new(Inherited + "ListerChild", "get_Name", Inherited + "ListerChild.get_Name()"),
            new(Inherited + "Middle", "get_Name", Inherited + "Middle.get_Name()", $" (for {Inherited}LateLister)"),
            new(Inherited + "PlainBase", "get_Name", Inherited + "PlainBase.get_Name()", $" (for {Inherited}Lister)"),
            new(Inherited + "Renamer", Inherited + "IPart.get_Name", Inherited + "Renamer." + Inherited + "IPart.get_Name()"),
        ]),
        ["IPart.Changed"] = new(Inherited + "IPart", "Changed", Inherited + "IPart.Changed",
        [
            new(Inherited + "Holder", "Changed", Inherited + "Holder.Changed"),
            new(Inherited + "PlainBase", "Changed", Inherited + "PlainBase.Changed", $" (for {Inherited}LateLister, {Inherited}Lister)"),
        ]),
        // Five classes bind ITag.Tag to TagBase's method; the line names three and counts the rest.
        ["ITag.Tag"] = new(Inherited + "ITag", "Tag", Inherited + "ITag.Tag()",
        [
            new(Inherited + "TagBase", "Tag", Inherited + "TagBase.Tag()",
                $" (for {Inherited}TagA, {Inherited}TagB, {Inherited}TagC and 2 more)"),
        ]),
        // Of the eleven members named Get below ISource, these three implement its Get.
        ["ISource.Get"] = new(Members + "ISource", "Get", Members + "ISource.Get()",
        [
            new(Members + "OverridingSource", "Get", Members + "OverridingSource.Get()"),
            new(Members + "Source", "Get", Members + "Source.Get()"),
            new(Members + "SplitSource", Members + "ISource.Get", Members + "SplitSource." + Members + "ISource.Get()"),
        ]),
        ["IShape.get_Name"] = new(Members + "IShape", "get_Name", Members + "IShape.get_Name()",
        [
            new(Members + "Circle", "get_Name", Members + "Circle." + Members + "IShape.get_Name()"),
        ]),
    };

    public static TheoryData<string> BindingNames => new(Bindings.Keys);

    [Theory]
    [MemberData(nameof(BindingNames))]
    public async Task ImplementedBy_ListsWhatClassesBindTheMemberTo_AndImplementsNamesItForEach(string name)
    {
        var binding = Bindings[name];
        int count = binding.Implementers.Length;

        string implementedBy = await Analyze(_shared.Client, _explicitMembersPath, binding.Interface, binding.AskedAs, "implemented_by");

        Assert.Equal(
            $"{count} implementation{(count == 1 ? "" : "s")} of {binding.Line} [declared]:\n" +
            string.Join("\n", binding.Implementers.Select(implementer => implementer.Line + implementer.BoundBy)),
            implementedBy);

        // The converse: each of them names the interface member, with the same classes.
        foreach (var implementer in binding.Implementers)
        {
            string implements = await Analyze(_shared.Client, _explicitMembersPath, implementer.Type, implementer.AskedAs, "implements");

            Assert.Equal(
                $"1 interface member implemented by {implementer.Line} [declared]:\n{binding.Line}{implementer.BoundBy}",
                implements);
        }
    }

    // Stranger overrides PlainBase's Name and never has IPart. BelowRenamer overrides Holder's
    // Name below a class that implements IPart.Name explicitly. Hider's Get hides Holder's,
    // and the class below that lists IPart again implements Get by a method of its own.
    [Theory]
    [InlineData("Stranger", "Name", "Name")]
    [InlineData("BelowRenamer", "Name", "Name")]
    [InlineData("Hider", "Get", "Get()")]
    public async Task Implements_OnAMemberNoClassBindsTheInterfaceTo_IsEmpty(string typeName, string memberName, string printed)
    {
        string text = await Analyze(_shared.Client, _explicitMembersPath, Inherited + typeName, memberName, "implements");

        Assert.Equal($"No interface members implemented by {Inherited}{typeName}.{printed} [declared]", text);
    }

    [Fact]
    public async Task ImplementedBy_ListsAMemberInheritedFromAnotherAssembly_OrSaysWhatWasNotRead()
    {
        // Probe declares nothing and inherits Level from ExternalLib's Sensor. Where that
        // assembly loads, Sensor's method is the line; where it does not, an empty answer
        // alone would read as proof that nothing implements Level.
        string loaded = await Analyze(_references.Client, _userPath, Reading, "Level", "implemented_by");
        string notLoaded = await Analyze(_shared.Client, _userPath, Reading, "Level", "implemented_by");

        Assert.Equal(
            $"1 implementation of {Reading}.Level() [declared]:\n" +
            "ExternalLib.Inherited.Sensor.Level() (for ExternalUser.Inherited.Probe)",
            loaded);
        Assert.Equal(
            $"No implementations of {Reading}.Level() [declared]\n" +
            "Not checked: what ExternalLib.Inherited.Sensor and its base types declare; ILens could not load " +
            "ExternalLib, Version=1.0.0.0",
            notLoaded);
    }

    // Sensor's Peak is not virtual, so the compiler adds a method to Probe that implements the
    // getter of IReading's property and passes the call on. No property owns that method, so it
    // stands for the property, and its own row names it whether or not ExternalLib loads.
    [Theory]
    [InlineData("Peak", "Peak")]
    [InlineData("get_Peak", "get_Peak()")]
    public async Task ImplementedBy_OnAPropertyBoundThroughAnAddedMethod_ListsThatMethod(string memberName, string printed)
    {
        const string added = "ExternalUser.Inherited.Probe.ExternalUser.Inherited.IReading.get_Peak()";
        string expected = $"1 implementation of {Reading}.{printed} [declared]:\n{added}";

        Assert.Equal(expected, await Analyze(_references.Client, _userPath, Reading, memberName, "implemented_by"));
        Assert.Equal(expected, await Analyze(_shared.Client, _userPath, Reading, memberName, "implemented_by"));
        Assert.Equal(
            $"1 interface member implemented by {added} [declared]:\n{Reading}.get_Peak()",
            await Analyze(_shared.Client, _userPath, "ExternalUser.Inherited.Probe", Reading + ".get_Peak", "implements"));
    }

    // The member that implements by its row is the line, whatever it is called, and a public
    // member with the interface member's name beside it is not. Accessor methods without a
    // property are listed for the property and for its accessor.
    [Theory]
    [InlineData("RowBound.IJob", "Run", "implemented_by",
        "1 implementation of RowBound.IJob.Run() [declared]:\nRowBound.Worker.Start()")]
    [InlineData("RowBound.Worker", "Start", "implements",
        "1 interface member implemented by RowBound.Worker.Start() [declared]:\nRowBound.IJob.Run()")]
    [InlineData("RowBound.Worker", "Run", "implements",
        "No interface members implemented by RowBound.Worker.Run() [declared]")]
    [InlineData("RowBound.IJob", "Size", "implemented_by",
        "1 implementation of RowBound.IJob.Size [declared]:\nRowBound.Worker.RowBound.IJob.get_Size()")]
    [InlineData("RowBound.IJob", "get_Size", "implemented_by",
        "1 implementation of RowBound.IJob.get_Size() [declared]:\nRowBound.Worker.RowBound.IJob.get_Size()")]
    [InlineData("RowBound.Worker", "RowBound.IJob.get_Size", "implements",
        "1 interface member implemented by RowBound.Worker.RowBound.IJob.get_Size() [declared]:\nRowBound.IJob.get_Size()")]
    public async Task AMemberThatImplementsByItsRowAlone_CountsInBothAnswers(
        string typeName, string memberName, string kind, string expected)
    {
        Assert.Equal(expected, await AnalyzeWritten(typeName, memberName, kind));
    }

    // A class binds an interface's members anew only where its own rows name the interface or
    // its base class does not have it. LateStep names ILateStep alone below Step, which has
    // the base interface IStep, as the VB compiler writes it: it keeps Step's binding of Order
    // to a method of another name, and its own Order implements nothing. LoneStep names
    // ILateStep alone without such a base class, and RenewedStep names both interfaces, so
    // each of the two binds IStep's Order to its own.
    [Theory]
    [InlineData("RowBound.IStep", "Order", "implemented_by",
        "3 implementations of RowBound.IStep.Order() [declared]:\n" +
        "RowBound.LoneStep.Order()\nRowBound.RenewedStep.Order()\nRowBound.Step.Step_Order()")]
    [InlineData("RowBound.LateStep", "Order", "implements",
        "No interface members implemented by RowBound.LateStep.Order() [declared]")]
    [InlineData("RowBound.Step", "Order", "implements",
        "No interface members implemented by RowBound.Step.Order() [declared]")]
    [InlineData("RowBound.Step", "Step_Order", "implements",
        "1 interface member implemented by RowBound.Step.Step_Order() [declared]:\nRowBound.IStep.Order()")]
    [InlineData("RowBound.LoneStep", "Order", "implements",
        "1 interface member implemented by RowBound.LoneStep.Order() [declared]:\nRowBound.IStep.Order()")]
    [InlineData("RowBound.RenewedStep", "Order", "implements",
        "1 interface member implemented by RowBound.RenewedStep.Order() [declared]:\nRowBound.IStep.Order()")]
    public async Task AClassBindsAnInterfaceAnew_OnlyWhereItsRowsNameItOrItsBaseClassLacksIt(
        string typeName, string memberName, string kind, string expected)
    {
        Assert.Equal(expected, await AnalyzeWritten(typeName, memberName, kind));
    }

    // The InterfaceShapes fixture's IBufferedFeed implements IFeed's Open with a body of its
    // own and makes Close abstract again. Both members name IFeed's by the same kind of row,
    // and the abstract one implements nothing.
    [Theory]
    [InlineData("InterfaceShapes.IFeed", "Open", "implemented_by",
        "1 implementation of InterfaceShapes.IFeed.Open() [declared]:\n" +
        "InterfaceShapes.IBufferedFeed.InterfaceShapes.IFeed.Open()")]
    [InlineData("InterfaceShapes.IFeed", "Close", "implemented_by",
        "No implementations of InterfaceShapes.IFeed.Close() [declared]")]
    [InlineData("InterfaceShapes.IBufferedFeed", "InterfaceShapes.IFeed.Open", "implements",
        "1 interface member implemented by InterfaceShapes.IBufferedFeed.InterfaceShapes.IFeed.Open() [declared]:\n" +
        "InterfaceShapes.IFeed.Open()")]
    [InlineData("InterfaceShapes.IBufferedFeed", "InterfaceShapes.IFeed.Close", "implements",
        "No interface members implemented by InterfaceShapes.IBufferedFeed.InterfaceShapes.IFeed.Close() [declared]")]
    public async Task AMemberADerivedInterfaceMakesAbstractAgain_ImplementsNothing(
        string typeName, string memberName, string kind, string expected)
    {
        Assert.Equal(expected, await Analyze(_shared.Client, _interfaceShapesPath, typeName, memberName, kind));
    }

    [Fact]
    public async Task Implements_OnTheMoveNextOfAGeneratedClass_NamesTheInterfaceMemberItsRowNames()
    {
        // The compiler writes MoveNext without the interface's name and not public, with a row
        // that names the interface's method.
        string types = await CallToolText(_shared.Client, "find_types", new()
        {
            ["assembly"] = _callShapesPath,
            ["name_contains"] = "d__",
            ["accessibility"] = "all",
            ["include_compiler_generated"] = true,
        });
        string generated = types.Split('\n').Skip(1).First(line => line.Contains("d__")).Split(' ')[^1];

        string text = await Analyze(_shared.Client, _callShapesPath, generated, "MoveNext", "implements");

        string[] lines = text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal($"1 interface member implemented by {generated}.MoveNext() [declared]:", lines[0]);
        Assert.Contains(lines[1], new[]
        {
            "System.Collections.IEnumerator.MoveNext()",
            "System.Runtime.CompilerServices.IAsyncStateMachine.MoveNext()",
        });
    }

    // Only a member of an interface is implemented. For a virtual member of a class the answer
    // points to its overrides; Holder's Get is not virtual.
    [Theory]
    [InlineData(Members + "Source", "Get",
        "Error: ArgumentException: Analysis kind 'implemented_by' on a member requires a member of an interface; " +
        "ExplicitMembers.Source.Get() is declared by the class ExplicitMembers.Source. 'overridden_by' lists its overrides.")]
    [InlineData(Inherited + "Holder", "Get",
        "Error: ArgumentException: Analysis kind 'implemented_by' on a member requires a member of an interface; " +
        "ExplicitMembers.Inherited.Holder.Get() is declared by the class ExplicitMembers.Inherited.Holder.")]
    public async Task ImplementedBy_OnAMemberOfAClass_IsAnError(string typeName, string memberName, string expected)
    {
        CallToolResult result = await _shared.Client.CallToolAsync("analyze", new Dictionary<string, object?>
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = "implemented_by",
        });

        Assert.True(result.IsError);
        Assert.Equal(expected, string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
    }

    /// <summary>
    /// The answer about the assembly <see cref="RowImplementationAssembly"/> writes, which is
    /// written for the one question and deleted after it.
    /// </summary>
    private async Task<string> AnalyzeWritten(string typeName, string memberName, string kind)
    {
        var path = RowImplementationAssembly.Write();
        try
        {
            return await Analyze(_shared.Client, path, typeName, memberName, kind);
        }
        finally
        {
            RowImplementationAssembly.TryDelete(path);
        }
    }

    private static Task<string> Analyze(McpClient client, string assembly, string typeName, string memberName, string kind) =>
        CallToolText(client, "analyze", new()
        {
            ["assembly"] = assembly,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        });

    private static async Task<string> CallToolText(McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
