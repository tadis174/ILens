using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for how an extension method is written and found. A declaration line writes
/// this before its first parameter, before ref or in as well, as C# and the decompiled code do, by
/// the rule the decompiler writes it by: a static method with ExtensionAttribute, whatever its
/// class. The same rule decides what the lookup from the type it extends finds, so a Visual Basic
/// module's extension method is found like a C# static class's, and a receiver passed by
/// reference is matched by the type it refers to. The reference form, which names a method so
/// that it can be passed on, leaves this out, as it leaves out params. A header that names an
/// extension method found from the type it extends names the method under the class that declares
/// it, and its mark names the type it extends. The fixtures are the namespace CallShapes.Receivers
/// in Tests/Fixtures/CallShapes/Receivers.cs, the Visual Basic module of Tests/Fixtures/VbModules
/// and ExternalUser's WidgetExtensions; compare's lines for a method that gains or loses this are
/// pinned in ComparisonTests.
/// <para>
/// Through a type another library defines, the lookup reads that type from its library: a member
/// of the name comes first where C# binds it for a call on a value from the assembly, a public
/// instance member or an internal one the library grants the assembly, also where a base interface
/// declares it, and then the extension methods for the type, its base classes and its interfaces.
/// With a hint, a member the hint fits comes first wherever the assembly can name it, a static and
/// a protected one included. call_sites and compare look for a method, so a property, field or
/// event of the name does not come first for them, as through a type of the assembly. Through a
/// class of the assembly, a member of another library's base class that its code cannot reach is
/// passed over as well. Where the library is not found, the members the assembly's references
/// name come first, apart from a static method where no hint is given, only an extension method
/// for the type itself is found, and the answer says what was not read. The fixtures are
/// ExternalLib's namespaces ExternalLib.Extended and ExternalLib.Access and ExternalUser's
/// ExternalUser.Extended and ExternalUser.Access, which the shared server reads without ExternalLib
/// and a second server with it, and the extension methods for String in BaseShift.Signatures,
/// which compare reads in both builds. Where only one build's version of the library has a member
/// of the name, compare says so, read with a version of ExternalLib that BareKettleAssembly writes.
/// AccessLibraryAssembly writes the versions that grant ExternalUser their internals or not, and
/// whose interface derives from an interface of an assembly no folder holds.
/// </para>
/// <para>
/// An extension method whose receiver is a type parameter is found through every type that fits
/// its constraints by C#'s rule, and its mark names the type parameter with them; extension_methods
/// lists it under the types its constraints name, or under System.ValueType or System.Object. The
/// fixtures are the namespace CallShapes.Constraints in Tests/Fixtures/CallShapes/Constraints.cs,
/// ExternalUser's VesselExtensions, read with and without ExternalLib and beside the version of
/// ExternalLib that BareKettleAssembly writes, and BaseShift.Constraints, whose Box fits in
/// BaseShift alone.
/// </para>
/// </summary>
public sealed class ExtensionMethodTests : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private const string Receivers = "CallShapes.Receivers";
    private const string Constraints = "CallShapes.Constraints";
    private const string ShapeExtensions = "CallShapes.Constraints.ShapeExtensions";
    private const string Kettle = "ExternalLib.Extended.Kettle";
    private const string KettleExtensions = "ExternalUser.Extended.KettleExtensions";
    private const string VesselExtensions = "ExternalUser.Extended.VesselExtensions";
    private const string Lantern = "ExternalLib.Access.Lantern";
    private const string TallStand = "ExternalLib.Access.ITallStand";
    private const string OwnShelf = "ExternalUser.Access.OwnShelf";
    private const string AccessExtensions = "ExternalUser.Access.AccessExtensions";
    private const string ShelfExtensions = "ExternalUser.Access.ShelfExtensions";
    private const string Keeper = "ExternalUser.Access.Keeper";

    private readonly ILensServerFixture _server;
    private readonly ReferencesServerFixture _references;
    private readonly string _callShapesPath;
    private readonly string _vbModulesPath;
    private readonly string _externalUserPath;

    public ExtensionMethodTests(ILensServerFixture server, ReferencesServerFixture references)
    {
        _server = server;
        _references = references;
        _callShapesPath = FixturePath("CallShapes");
        _vbModulesPath = FixturePath("VbModules");
        _externalUserPath = FixturePath("ExternalUser");
    }

    private string FixturePath(string fixture)
    {
        string path = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fixture + ".dll");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"{fixture} fixture not found: {path}. " +
                $"Tests/Fixtures/{fixture}/ is expected to land here via Tests.csproj's ProjectReference.");
        return path;
    }

    // Each receiver the class declares: a class, one with a params parameter after it, a struct
    // by ref and by in, and object; the static method that takes a Lamp without this keeps its line.
    [Fact]
    public async Task ListMembers_WritesThisBeforeTheReceiverOfEachExtensionMethod()
    {
        Assert.Equal(
            $"6 members of public static class {Receivers}.LampExtensions:\n" +
            "6 methods:\n" +
            "  public static void Brighten(this Lamp lamp, params int[] steps)\n" +
            "  public static void Dim(this Lamp lamp)\n" +
            "  public static void Drain(this ref Charge charge)\n" +
            "  public static string LabelOf(this object value)\n" +
            "  public static int Measure(this in Charge charge)\n" +
            "  public static void Replace(Lamp lamp)",
            await CallToolText("list_members", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = $"{Receivers}.LampExtensions",
            }));
    }

    // A line passes back as a method's name and parameter types, so it writes no this.
    [Fact]
    public async Task FindMethods_NamesAnExtensionMethodWithoutThis()
    {
        Assert.Equal(
            "6 methods:\n" +
            $"static {Receivers}.LampExtensions.Brighten(Lamp, int[]) → void\n" +
            $"static {Receivers}.LampExtensions.Dim(Lamp) → void\n" +
            $"static {Receivers}.LampExtensions.Drain(ref Charge) → void\n" +
            $"static {Receivers}.LampExtensions.LabelOf(object) → string\n" +
            $"static {Receivers}.LampExtensions.Measure(in Charge) → int\n" +
            $"static {Receivers}.LampExtensions.Replace(Lamp) → void",
            await CallToolText("find_methods", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = $"{Receivers}.LampExtensions",
            }));
    }

    // Asked on the class that declares it, the header names the method as find_methods does,
    // without this and without a mark, and the code shows this.
    [Fact]
    public async Task Decompile_OnTheClassThatDeclaresIt_NamesTheMethodWithoutThis()
    {
        string code = await CallToolText("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{Receivers}.LampExtensions",
            ["member_name"] = "Drain",
            ["parameter_types"] = new[] { "ref Charge" },
        });

        Assert.Equal($"// {Receivers}.LampExtensions.Drain(ref Charge) → void", code.Split('\n')[0]);
        Assert.Contains("public static void Drain(this ref Charge charge)", code);
    }

    // The type the method extends: the type asked about, a base class of it, or object.
    [Theory]
    [InlineData("Lamp", "Dim", $"// {Receivers}.LampExtensions.Dim(Lamp) → void [extension method on {Receivers}.Lamp]")]
    [InlineData("DeskLamp", "Dim", $"// {Receivers}.LampExtensions.Dim(Lamp) → void [extension method on {Receivers}.Lamp]")]
    [InlineData("DeskLamp", "LabelOf",
        $"// {Receivers}.LampExtensions.LabelOf(object) → string [extension method on System.Object]")]
    public async Task Decompile_AnExtensionMethodFoundFromATypeItExtends_NamesItUnderTheClassThatDeclaresIt(
        string typeName, string memberName, string header)
    {
        string code = await CallToolText("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{Receivers}.{typeName}",
            ["member_name"] = memberName,
        });

        Assert.Equal(header, code.Split('\n')[0]);
    }

    [Fact]
    public async Task Analyze_AnExtensionMethodFoundFromATypeItExtends_MarksItWithTheTypeItExtends()
    {
        Assert.Equal(
            $"No users of {Receivers}.LampExtensions.Dim(Lamp) [extension method on {Receivers}.Lamp]",
            await CallToolText("analyze", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = $"{Receivers}.DeskLamp",
                ["member_name"] = "Dim",
                ["kind"] = "used_by",
            }));
    }

    // A Visual Basic module compiles to a sealed class that is not static; its methods with
    // <Extension()> are written with this, and the one without keeps its line as it is.
    [Fact]
    public async Task ListMembers_WritesThisBeforeTheReceiverOfAVisualBasicModulesExtensionMethods()
    {
        Assert.Equal(
            "4 members of public sealed class VbModules.GizmoModule:\n" +
            "4 methods:\n" +
            "  public static void Plain(Gizmo gizmo)\n" +
            "  public static string Shout(this string text)\n" +
            "  public static void Turn(this ref Dial dial)\n" +
            "  public static void Wave(this Gizmo gizmo)",
            await CallToolText("list_members", new()
            {
                ["assembly"] = _vbModulesPath,
                ["type_name"] = "VbModules.GizmoModule",
            }));
    }

    // Each tool that looks a member up by name finds a Visual Basic module's extension method from
    // the type it extends, as it finds a C# static class's.
    [Fact]
    public async Task Decompile_FindsAVisualBasicModulesExtensionMethodFromTheTypeItExtends()
    {
        string code = await CallToolText("decompile", new()
        {
            ["assembly"] = _vbModulesPath,
            ["type_name"] = "VbModules.Gizmo",
            ["member_name"] = "Wave",
        });

        Assert.Equal(
            "// VbModules.GizmoModule.Wave(Gizmo) → void [extension method on VbModules.Gizmo]",
            code.Split('\n')[0]);
        Assert.Contains("public static void Wave(this Gizmo gizmo)", code);
    }

    [Fact]
    public async Task Analyze_FindsAVisualBasicModulesExtensionMethodFromTheTypeItExtends()
    {
        Assert.Equal(
            "1 user of VbModules.GizmoModule.Wave(Gizmo) [extension method on VbModules.Gizmo]:\n" +
            "VbModules.Caller.Use(Gizmo, Dial)",
            await CallToolText("analyze", new()
            {
                ["assembly"] = _vbModulesPath,
                ["type_name"] = "VbModules.Gizmo",
                ["member_name"] = "Wave",
                ["kind"] = "used_by",
            }));
    }

    [Fact]
    public async Task Compare_FindsAVisualBasicModulesExtensionMethodFromTheTypeItExtends()
    {
        Assert.Equal(
            "// VbModules.GizmoModule.Wave(Gizmo) → void [extension method on VbModules.Gizmo]" +
            " — bodies are identical in both assemblies.",
            await CallToolText("compare", new()
            {
                ["assembly_a"] = _vbModulesPath,
                ["assembly_b"] = _vbModulesPath,
                ["type_name"] = "VbModules.Gizmo",
                ["member_name"] = "Wave",
            }));
    }

    // A method without ExtensionAttribute extends nothing, whatever class declares it.
    [Fact]
    public async Task Decompile_DoesNotTakeAModulesMethodWithoutExtensionAttributeForOne()
    {
        var (isError, text) = await CallTool("decompile", new()
        {
            ["assembly"] = _vbModulesPath,
            ["type_name"] = "VbModules.Gizmo",
            ["member_name"] = "Plain",
        });

        Assert.True(isError);
        Assert.Equal(
            "Error: ArgumentException: Member 'Plain' not found on VbModules.Gizmo, its base types, " +
            "or as an extension method.",
            text);
    }

    // A receiver passed by reference is matched by the type it refers to: by ref and by in in C#,
    // ByRef in Visual Basic, and by ref readonly.
    [Theory]
    [InlineData("CallShapes", $"{Receivers}.Charge", "Drain",
        $"// {Receivers}.LampExtensions.Drain(ref Charge) → void [extension method on {Receivers}.Charge]")]
    [InlineData("CallShapes", $"{Receivers}.Charge", "Measure",
        $"// {Receivers}.LampExtensions.Measure(in Charge) → int [extension method on {Receivers}.Charge]")]
    [InlineData("VbModules", "VbModules.Dial", "Turn",
        "// VbModules.GizmoModule.Turn(ref Dial) → void [extension method on VbModules.Dial]")]
    [InlineData("ExternalUser", "ExternalUser.Slot", "Span",
        "// ExternalUser.WidgetExtensions.Span(ref readonly Slot) → int [extension method on ExternalUser.Slot]")]
    public async Task Decompile_FindsAnExtensionMethodWhoseReceiverIsPassedByReference(
        string fixture, string typeName, string memberName, string header)
    {
        string code = await CallToolText("decompile", new()
        {
            ["assembly"] = FixturePath(fixture),
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });

        Assert.Equal(header, code.Split('\n')[0]);
    }

    // The extension methods of a type come from the table the lookup uses, for a type the
    // assembly defines and for one of another assembly, by value and by reference. A receiver
    // that ILSpy reads as nint is listed for System.IntPtr.
    [Theory]
    [InlineData("VbModules", "VbModules.Gizmo",
        "1 extension method for VbModules.Gizmo:\nVbModules.GizmoModule.Wave(Gizmo)")]
    [InlineData("VbModules", "VbModules.Dial",
        "1 extension method for VbModules.Dial:\nVbModules.GizmoModule.Turn(ref Dial)")]
    [InlineData("VbModules", "System.String",
        "1 extension method for System.String:\nVbModules.GizmoModule.Shout(string)")]
    [InlineData("CallShapes", $"{Receivers}.Charge",
        $"2 extension methods for {Receivers}.Charge:\n" +
        $"{Receivers}.LampExtensions.Drain(ref Charge)\n{Receivers}.LampExtensions.Measure(in Charge)")]
    [InlineData("ExternalUser", "ExternalUser.Slot",
        "1 extension method for ExternalUser.Slot:\nExternalUser.WidgetExtensions.Span(ref readonly Slot)")]
    [InlineData("ExternalUser", "ExternalLib.Point",
        "1 extension method for ExternalLib.Point:\nExternalUser.WidgetExtensions.Nudge(ref Point)")]
    [InlineData("ExternalUser", "System.IntPtr",
        "1 extension method for System.IntPtr:\nExternalUser.WidgetExtensions.Offset(nint)")]
    public async Task Analyze_ExtensionMethods_ListsThoseOfEveryClassWithTheirReceiverByValueOrByReference(
        string fixture, string typeName, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText("analyze", new()
            {
                ["assembly"] = FixturePath(fixture),
                ["type_name"] = typeName,
                ["kind"] = "extension_methods",
            }));
    }

    // An extension method whose receiver is a type parameter is found through every type that fits
    // its constraints by C#'s rule, and its mark names the type parameter with them: class through
    // a class, struct and IShape by reference through a struct, IShape through a class whose base
    // class has it, none through an enum, struct alone, struct and System.Enum, and new() through a
    // class with a public constructor.
    [Theory]
    [InlineData("Crate", "Tag", $"// {ShapeExtensions}.Tag<T>(T) → T [extension method on T where T : class]")]
    [InlineData("Spot", "Grow",
        $"// {ShapeExtensions}.Grow<T>(ref T) → void [extension method on T where T : struct, {Constraints}.IShape]")]
    [InlineData("Pebble", "Corners",
        $"// {ShapeExtensions}.Corners<T>(T) → int [extension method on T where T : {Constraints}.IShape]")]
    [InlineData("Hue", "Echo", $"// {ShapeExtensions}.Echo<T>(T) → T [extension method on T]")]
    [InlineData("Spot", "Zero", $"// {ShapeExtensions}.Zero<T>(T) → T [extension method on T where T : struct]")]
    [InlineData("Hue", "Spell",
        $"// {ShapeExtensions}.Spell<T>(T) → string [extension method on T where T : struct, System.Enum]")]
    [InlineData("Crate", "Fresh", $"// {ShapeExtensions}.Fresh<T>(T) → T [extension method on T where T : new()]")]
    public async Task Decompile_ThroughATypeThatFitsItsConstraints_FindsAnExtensionMethodOnATypeParameter(
        string typeName, string memberName, string header)
    {
        string code = await CallToolText("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{Constraints}.{typeName}",
            ["member_name"] = memberName,
        });

        Assert.Equal(header, code.Split('\n')[0]);
    }

    // A type that does not fit keeps the answer it had: a struct for class, a class for struct, a
    // class without IShape, a class for struct and System.Enum, and an abstract class for new().
    [Theory]
    [InlineData("Spot", "Tag")]
    [InlineData("Plate", "Grow")]
    [InlineData("Crate", "Corners")]
    [InlineData("Crate", "Spell")]
    [InlineData("Mold", "Fresh")]
    public async Task Decompile_ThroughATypeThatDoesNotFitItsConstraints_DoesNotFindTheExtensionMethod(
        string typeName, string memberName)
    {
        var (isError, text) = await CallTool("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{Constraints}.{typeName}",
            ["member_name"] = memberName,
        });

        Assert.True(isError);
        Assert.Equal(
            $"Error: ArgumentException: Member '{memberName}' not found on {Constraints}.{typeName}, its base types, " +
            "or as an extension method.",
            text);
    }

    // Such a method always has a type parameter, so a name without brackets means the method of
    // the name without one where both fit, and the brackets reach the other. Through Spot only the
    // generic one fits.
    [Theory]
    [InlineData("Plate", "Fit", $"// {ShapeExtensions}.Fit(Plate) → void [extension method on {Constraints}.Plate]")]
    [InlineData("Plate", "Fit<T>",
        $"// {ShapeExtensions}.Fit<T>(T) → void [extension method on T where T : {Constraints}.IShape]")]
    [InlineData("Spot", "Fit",
        $"// {ShapeExtensions}.Fit<T>(T) → void [extension method on T where T : {Constraints}.IShape]")]
    public async Task Decompile_ANameAnExtensionMethodOnATypeParameterShares_MeansTheMethodWithoutTypeParameters(
        string typeName, string memberName, string header)
    {
        string code = await CallToolText("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{Constraints}.{typeName}",
            ["member_name"] = memberName,
        });

        Assert.Equal(header, code.Split('\n')[0]);
    }

    // A member of the name comes before any extension method, as in C#, also where several
    // extension methods of the name fit, whose receivers are a type parameter (Shade on Plate) or
    // the type itself (Label on Crate); a hint asks for a method and reaches one of them.
    [Theory]
    [InlineData("Plate", "Shade", null, $"// {Constraints}.Plate.Shade")]
    [InlineData("Crate", "Label", null, $"// {Constraints}.Crate.Label")]
    [InlineData("Plate", "Shade", "int",
        $"// {ShapeExtensions}.Shade<T>(T, int) → T [extension method on T where T : {Constraints}.IShape]")]
    [InlineData("Crate", "Label", "string", $"// {ShapeExtensions}.Label(Crate, string) → Crate [extension method on {Constraints}.Crate]")]
    public async Task Decompile_ANameAMemberAndSeveralExtensionMethodsShare_MeansTheMember(
        string typeName, string memberName, string? secondParameter, string header)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = $"{Constraints}.{typeName}",
            ["member_name"] = memberName,
        };
        if (secondParameter is not null)
            arguments["parameter_types"] = new[] { typeName == "Plate" ? "T" : "Crate", secondParameter };

        string code = await CallToolText("decompile", arguments);

        Assert.Equal(header, code.Split('\n')[0]);
    }

    [Fact]
    public async Task Analyze_UsedBy_ANameAMemberAndSeveralExtensionMethodsShare_MeansTheMember()
    {
        Assert.Equal(
            $"No users of {Constraints}.Crate.Label [declared] (via get_Label, set_Label)",
            await CallToolText("analyze", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = $"{Constraints}.Crate",
                ["member_name"] = "Label",
                ["kind"] = "used_by",
            }));
    }

    [Theory]
    [InlineData("used_by", "Pebble", "Corners",
        $"1 user of {ShapeExtensions}.Corners<T>(T) [extension method on T where T : {Constraints}.IShape]:\n" +
        $"{Constraints}.Caller.Use(Plate, Pebble, Spot, Crate, Hue)")]
    [InlineData("call_sites", "Spot", "Grow",
        $"1 call to {ShapeExtensions}.Grow<T>(ref T):\n{Constraints}.Caller.Use(Plate, Pebble, Spot, Crate, Hue)\n" +
        "  ShapeExtensions.Grow(ref spot)")]
    [InlineData("uses", "Hue", "Spell",
        $"1 symbol used by {ShapeExtensions}.Spell<T>(T) [extension method on T where T : struct, System.Enum]:\n" +
        "System.Object.ToString()")]
    public async Task Analyze_ThroughATypeThatFitsItsConstraints_AnswersForTheExtensionMethodOnATypeParameter(
        string kind, string typeName, string memberName, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText("analyze", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = $"{Constraints}.{typeName}",
                ["member_name"] = memberName,
                ["kind"] = kind,
            }));
    }

    // extension_methods lists such a method under each type its constraints name, as if that type
    // were its receiver, and one whose constraints name no type under System.ValueType where it has
    // struct and under System.Object otherwise. Plate lists only the method whose receiver is Plate
    // itself, and no type lists a method through a base type or a type parameter it fits.
    [Theory]
    [InlineData($"{Constraints}.IShape",
        $"5 extension methods for {Constraints}.IShape:\n" +
        $"{ShapeExtensions}.Corners<T>(T)\n{ShapeExtensions}.Fit<T>(T)\n{ShapeExtensions}.Grow<T>(ref T)\n" +
        $"{ShapeExtensions}.Shade<T>(T, int)\n{ShapeExtensions}.Shade<T>(T, string)")]
    [InlineData($"{Constraints}.Plate", $"1 extension method for {Constraints}.Plate:\n{ShapeExtensions}.Fit(Plate)")]
    [InlineData($"{Constraints}.Pebble", $"No extension methods for {Constraints}.Pebble")]
    [InlineData("System.ValueType", $"1 extension method for System.ValueType:\n{ShapeExtensions}.Zero<T>(T)")]
    [InlineData("System.Enum", $"1 extension method for System.Enum:\n{ShapeExtensions}.Spell<T>(T)")]
    public async Task Analyze_ExtensionMethods_ListsAMethodOnATypeParameterUnderTheTypesItsConstraintsName(
        string typeName, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText("analyze", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = typeName,
                ["kind"] = "extension_methods",
            }));
    }

    // Tag's class and Fresh's new() name no type, and Echo has no constraint; the methods whose
    // receiver is object stand beside them.
    [Fact]
    public async Task Analyze_ExtensionMethods_ListsAMethodWhoseConstraintsNameNoTypeUnderObject()
    {
        string[] lines = (await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "System.Object",
            ["kind"] = "extension_methods",
        })).Split('\n');

        Assert.Contains($"{ShapeExtensions}.Echo<T>(T)", lines);
        Assert.Contains($"{ShapeExtensions}.Fresh<T>(T)", lines);
        Assert.Contains($"{ShapeExtensions}.Tag<T>(T)", lines);
        Assert.Contains($"{Receivers}.LampExtensions.LabelOf(object)", lines);
        Assert.DoesNotContain($"{ShapeExtensions}.Zero<T>(T)", lines);
    }

    // Box fits Corners in BaseShift alone, since BaseShift2's Box does not implement IShape. B
    // declares the method all the same, so the answer does not say only A has it. Crate fits in
    // both.
    [Fact]
    public async Task Compare_ThroughATypeThatFitsInOneBuildAlone_SaysTheOtherBuildDeclaresTheMethod()
    {
        const string header = "// BaseShift.Constraints.ShapeExtensions.Corners<T>(T) → int " +
            "[extension method on T where T : BaseShift.Constraints.IShape]";
        var builds = new Dictionary<string, object?>
        {
            ["assembly_a"] = FixturePath("BaseShift"),
            ["assembly_b"] = FixturePath("BaseShift2"),
            ["member_name"] = "Corners",
        };

        string box = await CallToolText("compare", new(builds) { ["type_name"] = "BaseShift.Constraints.Box" });
        Assert.StartsWith(
            $"{header} — only A finds it through BaseShift.Constraints.Box; " +
            "in B BaseShift.Constraints.Box does not meet the method's constraints.\n\n",
            box);
        Assert.Contains("public static int Corners<T>(this T shape) where T : IShape", box);

        Assert.Equal(
            $"{header} — bodies are identical in both assemblies.",
            await CallToolText("compare", new(builds) { ["type_name"] = "BaseShift.Constraints.Crate" }));
    }

    // Through a type another library defines, read from that library by the second server: an
    // extension method for the type, for its base class Appliance, for its interface IVessel, and
    // for the class TravelKettle derives from. Kettle's Descale is private, which this assembly
    // cannot reach, so it does not keep the extension method out.
    [Theory]
    [InlineData(Kettle, "Boil", $"// {KettleExtensions}.Boil(Kettle) → void [extension method on {Kettle}]")]
    [InlineData(Kettle, "Descale", $"// {KettleExtensions}.Descale(Kettle) → void [extension method on {Kettle}]")]
    [InlineData(Kettle, "Unplug",
        $"// {KettleExtensions}.Unplug(Appliance) → void [extension method on ExternalLib.Extended.Appliance]")]
    [InlineData(Kettle, "Measure",
        $"// {KettleExtensions}.Measure(IVessel) → int [extension method on ExternalLib.Extended.IVessel]")]
    [InlineData("ExternalLib.Extended.TravelKettle", "Boil",
        $"// {KettleExtensions}.Boil(Kettle) → void [extension method on {Kettle}]")]
    public async Task Decompile_ThroughAnotherLibrarysType_FindsTheExtensionMethodForItOrWhatItDerivesFrom(
        string typeName, string memberName, string header)
    {
        string code = await CallToolText(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });

        Assert.Equal(header, code.Split('\n')[0]);
        Assert.Contains("public static ", code);
        Assert.DoesNotContain("Not checked", code);
    }

    // Each kind is answered as through the class that declares the method.
    [Theory]
    [InlineData("used_by",
        $"1 user of {KettleExtensions}.Boil(Kettle) [extension method on {Kettle}]:\nExternalUser.Extended.Cook.Brew(Kettle)")]
    [InlineData("call_sites",
        $"1 call to {KettleExtensions}.Boil(Kettle):\nExternalUser.Extended.Cook.Brew(Kettle)\n  KettleExtensions.Boil(kettle)")]
    [InlineData("uses",
        $"1 symbol used by {KettleExtensions}.Boil(Kettle) [extension method on {Kettle}]:\n" +
        "ExternalLib.Extended.Appliance.Plug()")]
    public async Task Analyze_ThroughAnotherLibrarysType_AnswersForTheExtensionMethod(string kind, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText(_references.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = Kettle,
                ["member_name"] = "Boil",
                ["kind"] = kind,
            }));
    }

    [Fact]
    public async Task Compare_ThroughAnotherLibrarysType_GivesTheVerdictOnTheExtensionMethod()
    {
        Assert.Equal(
            $"// {KettleExtensions}.Boil(Kettle) → void [extension method on {Kettle}] — " +
            "bodies are identical in both assemblies.",
            await CallToolText(_references.Client, "compare", new()
            {
                ["assembly_a"] = _externalUserPath,
                ["assembly_b"] = _externalUserPath,
                ["type_name"] = Kettle,
                ["member_name"] = "Boil",
            }));
    }

    // BaseShift2 gives Weigh, an extension method for the framework's String, another body, and
    // only BaseShift2 has Echo. Both builds read String from the installed .NET.
    [Fact]
    public async Task Compare_ThroughAFrameworkType_ShowsTheDiffOfAnExtensionMethodsBodies()
    {
        string text = await CallToolText(_server.Client, "compare", new()
        {
            ["assembly_a"] = FixturePath("BaseShift"),
            ["assembly_b"] = FixturePath("BaseShift2"),
            ["type_name"] = "System.String",
            ["member_name"] = "Weigh",
        });

        Assert.StartsWith(
            "// BaseShift.Signatures.Extensions.Weigh(string) → int [extension method on System.String]\n\n--- A: ",
            text);
        Assert.Contains("\n+\treturn text.Length * 2;", text);
    }

    [Fact]
    public async Task Compare_ThroughAFrameworkType_ShowsAnExtensionMethodOnlyOneBuildHas()
    {
        string text = await CallToolText(_server.Client, "compare", new()
        {
            ["assembly_a"] = FixturePath("BaseShift"),
            ["assembly_b"] = FixturePath("BaseShift2"),
            ["type_name"] = "System.String",
            ["member_name"] = "Echo",
        });

        Assert.StartsWith(
            "// BaseShift.Signatures.Extensions.Echo(string) → string [extension method on System.String] " +
            "— only B has it.\n\n",
            text);
        Assert.Contains("public static string Echo(this string text)", text);
    }

    // Each copy of ExternalUser lies beside its own version of ExternalLib: A's Kettle has no member
    // Pour, and B's has Pour(int), which C# takes before the extension method of that name. B
    // declares the extension method all the same, so the answer does not say only A has it.
    [Fact]
    public async Task Compare_ThroughAnotherLibrarysType_SaysWhereAMemberOfTheTypeComesFirst()
    {
        string rootA = NewFolder();
        string rootB = NewFolder();
        File.Copy(_externalUserPath, Path.Combine(rootA, "ExternalUser.dll"));
        BareKettleAssembly.Write(rootA);
        File.Copy(_externalUserPath, Path.Combine(rootB, "ExternalUser.dll"));
        File.Copy(Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, "References", "ExternalLib.dll"),
            Path.Combine(rootB, "ExternalLib.dll"));
        try
        {
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-two-library-versions",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", rootA, "--allow-root", rootB],
            }));
            string text = await CallToolText(client, "compare", new()
            {
                ["assembly_a"] = Path.Combine(rootA, "ExternalUser.dll"),
                ["assembly_b"] = Path.Combine(rootB, "ExternalUser.dll"),
                ["type_name"] = Kettle,
                ["member_name"] = "Pour",
            });

            Assert.StartsWith(
                $"// {KettleExtensions}.Pour(Kettle, string) → void [extension method on {Kettle}] — only A finds it " +
                $"through {Kettle}; in B the type has a member of that name, which C# takes first.\n\n",
                text);
            Assert.Contains("public static void Pour(this Kettle kettle, string drink)", text);
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(rootA);
            await ILensServerFixture.DeleteWhenReleased(rootB);
        }
    }

    // A name Kettle has as a member means that member, as for a type the assembly defines: a
    // method that takes other parameters, and a property. ExternalUser has an extension method of
    // each name, and Cook.Brew calls it.
    [Theory]
    [InlineData("Pour", null, "1 user of ExternalLib.Extended.Kettle.Pour(int):\nExternalUser.Extended.Cook.Serve(Kettle)")]
    [InlineData("Pour", "int", "1 user of ExternalLib.Extended.Kettle.Pour(int):\nExternalUser.Extended.Cook.Serve(Kettle)")]
    [InlineData("Hot", null,
        "No users of ExternalLib.Extended.Kettle.Hot\n" +
        "This assembly references no member Hot of ExternalLib.Extended.Kettle. It references: Pour, Reset, Volume.")]
    public async Task Analyze_ThroughAnotherLibrarysType_ANameItHasAsAMember_MeansTheMember(
        string memberName, string? parameterType, string expected)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Kettle,
            ["member_name"] = memberName,
            ["kind"] = "used_by",
        };
        if (parameterType is not null)
            arguments["parameter_types"] = new[] { parameterType };

        Assert.Equal(expected, await CallToolText(_references.Client, "analyze", arguments));
    }

    [Fact]
    public async Task Decompile_ThroughAnotherLibrarysType_ANameItHasAsAMember_SaysWhereTheTypeComesFrom()
    {
        var (isError, text) = await CallTool(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Kettle,
            ["member_name"] = "Pour",
        });

        Assert.True(isError);
        Assert.Equal(
            $"Error: ArgumentException: {Kettle} is not defined in this assembly; it comes from ExternalLib, Version=1.0.0.0.",
            text);
    }

    // Parameter types that fit an extension method and no method of Kettle select the extension
    // method, beside a method of another signature and beside a property.
    [Theory]
    [InlineData("Pour", new[] { "Kettle", "string" },
        $"1 user of {KettleExtensions}.Pour(Kettle, string) [extension method on {Kettle}]:\nExternalUser.Extended.Cook.Brew(Kettle)")]
    [InlineData("Hot", new[] { "Kettle" },
        $"1 user of {KettleExtensions}.Hot(Kettle) [extension method on {Kettle}]:\nExternalUser.Extended.Cook.Brew(Kettle)")]
    public async Task Analyze_ThroughAnotherLibrarysType_ParameterTypesThatFitOnlyTheExtensionMethod_SelectIt(
        string memberName, string[] parameterTypes, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText(_references.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = Kettle,
                ["member_name"] = memberName,
                ["parameter_types"] = parameterTypes,
                ["kind"] = "used_by",
            }));
    }

    [Fact]
    public async Task Analyze_CallSites_ThroughAnotherLibrarysType_ListsTheArgumentForAParameterOfTheExtensionMethod()
    {
        Assert.Equal(
            $"1 call to {KettleExtensions}.Pour(Kettle, string), argument for drink:\n" +
            "ExternalUser.Extended.Cook.Brew(Kettle)\n  \"tea\"",
            await CallToolText(_references.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = Kettle,
                ["member_name"] = "Pour",
                ["parameter_types"] = new[] { "Kettle", "string" },
                ["parameter_name"] = "drink",
                ["kind"] = "call_sites",
            }));
    }

    // The shared server cannot load ExternalLib, so it knows neither Kettle's members, apart from
    // those ExternalUser's references name, nor what Kettle derives from. An extension method for
    // Kettle itself answers, and the answer says what was not read: under the header in decompile,
    // at the end in analyze, and first in compare.
    [Fact]
    public async Task Decompile_ThroughAnotherLibrarysTypeItCannotLoad_SaysWhatItDidNotRead()
    {
        string code = await CallToolText(_server.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Kettle,
            ["member_name"] = "Boil",
        });

        string[] lines = code.Split('\n');
        Assert.Equal($"// {KettleExtensions}.Boil(Kettle) → void [extension method on {Kettle}]", lines[0]);
        Assert.Equal("// " + NotChecked(Kettle, "Boil"), lines[1]);
    }

    [Theory]
    [InlineData("used_by",
        $"1 user of {KettleExtensions}.Boil(Kettle) [extension method on {Kettle}]:\nExternalUser.Extended.Cook.Brew(Kettle)")]
    [InlineData("call_sites",
        $"1 call to {KettleExtensions}.Boil(Kettle):\nExternalUser.Extended.Cook.Brew(Kettle)\n  KettleExtensions.Boil(kettle)")]
    [InlineData("uses",
        $"1 symbol used by {KettleExtensions}.Boil(Kettle) [extension method on {Kettle}]:\n" +
        "ExternalLib.Extended.Appliance.Plug()")]
    public async Task Analyze_ThroughAnotherLibrarysTypeItCannotLoad_EndsWithWhatItDidNotRead(string kind, string expected)
    {
        Assert.Equal(
            expected + "\n" + NotChecked(Kettle, "Boil"),
            await CallToolText(_server.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = Kettle,
                ["member_name"] = "Boil",
                ["kind"] = kind,
            }));
    }

    [Fact]
    public async Task Compare_ThroughAnotherLibrarysTypeItCannotLoad_SaysFirstWhatItDidNotRead()
    {
        Assert.Equal(
            "// " + NotChecked(Kettle, "Boil") + "\n" +
            $"// {KettleExtensions}.Boil(Kettle) → void [extension method on {Kettle}] — " +
            "bodies are identical in both assemblies.",
            await CallToolText(_server.Client, "compare", new()
            {
                ["assembly_a"] = _externalUserPath,
                ["assembly_b"] = _externalUserPath,
                ["type_name"] = Kettle,
                ["member_name"] = "Boil",
            }));
    }

    // Without ExternalLib, Appliance is not known as Kettle's base class, so Unplug is found only
    // through Appliance itself.
    [Fact]
    public async Task Decompile_ThroughAnotherLibrarysTypeItCannotLoad_FindsOnlyAnExtensionMethodForTheTypeItself()
    {
        var (isError, text) = await CallTool(_server.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Kettle,
            ["member_name"] = "Unplug",
        });
        Assert.True(isError);
        Assert.Equal(
            $"Error: ArgumentException: {Kettle} is not defined in this assembly; it comes from ExternalLib, Version=1.0.0.0.",
            text);

        string code = await CallToolText(_server.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Extended.Appliance",
            ["member_name"] = "Unplug",
        });
        string[] lines = code.Split('\n');
        Assert.Equal(
            $"// {KettleExtensions}.Unplug(Appliance) → void [extension method on ExternalLib.Extended.Appliance]",
            lines[0]);
        Assert.Equal("// " + NotChecked("ExternalLib.Extended.Appliance", "Unplug"), lines[1]);
    }

    // ExternalUser's references name Kettle's Pour(int), which keeps its answer as with ExternalLib
    // loaded, and Kettle's static Reset(), whose row says it takes no instance, so the extension
    // method answers there, as it does with ExternalLib loaded. They name no member Hot, so the
    // extension method answers there, where with ExternalLib loaded Kettle's property Hot does.
    // They name Lantern's Polish, which Lamplighter calls from a class derived from Lantern; no row
    // says that Polish is protected, so it answers, where with ExternalLib loaded the extension
    // method does.
    [Theory]
    [InlineData(Kettle, "Pour", "1 user of ExternalLib.Extended.Kettle.Pour(int):\nExternalUser.Extended.Cook.Serve(Kettle)")]
    [InlineData(Kettle, "Reset",
        $"1 user of {KettleExtensions}.Reset(Kettle) [extension method on {Kettle}]:\nExternalUser.Extended.Cook.Brew(Kettle)\n" +
        "Not checked: whether ExternalLib.Extended.Kettle or its base types have a member Reset, which C# would take first; " +
        "ILens could not load ExternalLib, Version=1.0.0.0")]
    [InlineData(Kettle, "Hot",
        $"1 user of {KettleExtensions}.Hot(Kettle) [extension method on {Kettle}]:\nExternalUser.Extended.Cook.Brew(Kettle)\n" +
        "Not checked: whether ExternalLib.Extended.Kettle or its base types have a member Hot, which C# would take first; " +
        "ILens could not load ExternalLib, Version=1.0.0.0")]
    [InlineData(Lantern, "Polish", $"1 user of {Lantern}.Polish():\nExternalUser.Access.Lamplighter.Clean()")]
    public async Task Analyze_ThroughAnotherLibrarysTypeItCannotLoad_AMemberTheReferencesName_MeansTheMember(
        string typeName, string memberName, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText(_server.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = typeName,
                ["member_name"] = memberName,
                ["kind"] = "used_by",
            }));
    }

    // WidgetExtensions.Polish extends ExternalLib's Widget, and nothing calls it.
    [Fact]
    public async Task Analyze_ThroughAnotherLibrarysTypeItCannotLoad_AnswersForAnExtensionMethodNothingCalls()
    {
        Assert.Equal(
            "No users of ExternalUser.WidgetExtensions.Polish(Widget) [extension method on ExternalLib.Widget]\n" +
            NotChecked("ExternalLib.Widget", "Polish"),
            await CallToolText(_server.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = "ExternalLib.Widget",
                ["member_name"] = "Polish",
                ["kind"] = "used_by",
            }));
    }

    // Through a type of another library that the second server reads, an extension method whose
    // receiver is a type parameter is found where the type fits its constraints, as through a type
    // the assembly defines: Fill asks for IVessel, which Kettle has, and Label fits every type.
    [Theory]
    [InlineData("Fill", $"// {VesselExtensions}.Fill<T>(T) → int [extension method on T where T : ExternalLib.Extended.IVessel]")]
    [InlineData("Label", $"// {VesselExtensions}.Label<T>(T) → string [extension method on T]")]
    public async Task Decompile_ThroughAnotherLibrarysTypeThatFitsItsConstraints_FindsAnExtensionMethodOnATypeParameter(
        string memberName, string header)
    {
        string code = await CallToolText(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Kettle,
            ["member_name"] = memberName,
        });

        Assert.Equal(header, code.Split('\n')[0]);
        Assert.DoesNotContain("Not checked:", code);
    }

    // Without ExternalLib, what Kettle is and derives from is not known, so only Label, which every
    // type fits, is found, with the line that says what was not read; Fill keeps the answer it had.
    [Fact]
    public async Task Decompile_ThroughAnotherLibrarysTypeItCannotLoad_FindsOnlyAMethodEveryTypeFits()
    {
        string code = await CallToolText("decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Kettle,
            ["member_name"] = "Label",
        });
        string[] lines = code.Split('\n');
        Assert.Equal($"// {VesselExtensions}.Label<T>(T) → string [extension method on T]", lines[0]);
        Assert.Equal("// " + NotChecked(Kettle, "Label"), lines[1]);

        var (isError, text) = await CallTool("decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Kettle,
            ["member_name"] = "Fill",
        });
        Assert.True(isError);
        Assert.Equal(
            $"Error: ArgumentException: {Kettle} is not defined in this assembly; it comes from ExternalLib, Version=1.0.0.0.",
            text);
    }

    // DerivedWidget is a class of ExternalUser whose base class Widget ExternalLib defines, which
    // the shared server cannot load. An extension method found through it, Label whose receiver is
    // a type parameter and Polish whose receiver is Widget, may stand where a member of Widget
    // would answer, as Widget's property Label does in C#, so the answer says Widget was not read:
    // under the header in decompile, at the end in analyze.
    [Theory]
    [InlineData("Label", $"// {VesselExtensions}.Label<T>(T) → string [extension method on T]")]
    [InlineData("Polish", "// ExternalUser.WidgetExtensions.Polish(Widget) → void [extension method on ExternalLib.Widget]")]
    public async Task Decompile_ThroughATypeWhoseBaseClassItCannotLoad_SaysTheBaseClassWasNotRead(
        string memberName, string header)
    {
        string[] lines = (await CallToolText("decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["member_name"] = memberName,
        })).Split('\n');

        Assert.Equal(header, lines[0]);
        Assert.Equal("// " + NotChecked("ExternalLib.Widget", memberName), lines[1]);
    }

    [Fact]
    public async Task Analyze_ThroughATypeWhoseBaseClassItCannotLoad_EndsWithTheBaseClassNotRead()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalUser.DerivedWidget",
            ["member_name"] = "Label",
            ["kind"] = "used_by",
        });

        Assert.Equal(
            $"1 user of {VesselExtensions}.Label<T>(T) [extension method on T]:\nExternalUser.Extended.Pantry.Stock(Kettle)\n" +
            NotChecked("ExternalLib.Widget", "Label"),
            text);
    }

    // Each copy of ExternalUser lies beside its own version of ExternalLib, and A's Kettle has no
    // IVessel. A declares Fill all the same, so the answer does not say only B has it.
    [Fact]
    public async Task Compare_ThroughAnotherLibrarysTypeThatFitsInOneBuildAlone_SaysTheOtherBuildDeclaresTheMethod()
    {
        string rootA = NewFolder();
        string rootB = NewFolder();
        File.Copy(_externalUserPath, Path.Combine(rootA, "ExternalUser.dll"));
        BareKettleAssembly.Write(rootA);
        File.Copy(_externalUserPath, Path.Combine(rootB, "ExternalUser.dll"));
        File.Copy(Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, "References", "ExternalLib.dll"),
            Path.Combine(rootB, "ExternalLib.dll"));
        try
        {
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-two-library-versions",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", rootA, "--allow-root", rootB],
            }));
            string text = await CallToolText(client, "compare", new()
            {
                ["assembly_a"] = Path.Combine(rootA, "ExternalUser.dll"),
                ["assembly_b"] = Path.Combine(rootB, "ExternalUser.dll"),
                ["type_name"] = Kettle,
                ["member_name"] = "Fill",
            });

            Assert.StartsWith(
                $"// {VesselExtensions}.Fill<T>(T) → int [extension method on T where T : ExternalLib.Extended.IVessel] — " +
                $"only B finds it through {Kettle}; in A {Kettle} does not meet the method's constraints.\n\n",
                text);
            Assert.Contains("public static int Fill<T>(this T vessel) where T : IVessel", text);
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(rootA);
            await ILensServerFixture.DeleteWhenReleased(rootB);
        }
    }

    // Both builds define Shop.Gauge, whose base class, ExternalLib's Widget, is not read without
    // ExternalLib. Shine is a property of Gauge in A and an extension method for Gauge in B, before
    // which C# would take a member of Widget of that name, so the answer says first what was not read.
    [Fact]
    public async Task Compare_ANameOfAnotherKindInEachBuild_ThroughATypeWhoseBaseClassWasNotRead_SaysFirstWhatItDidNotRead()
    {
        string rootA = NewFolder();
        string rootB = NewFolder();
        string library = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, "References", "ExternalLib.dll");
        string a = KindShiftAssembly.Write(rootA, withProperty: true, library);
        string b = KindShiftAssembly.Write(rootB, withProperty: false, library);
        try
        {
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-kind-shift",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", rootA, "--allow-root", rootB],
            }));
            string text = await CallToolText(client, "compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = "Shop.Gauge",
                ["member_name"] = "Shine",
            });

            Assert.StartsWith(
                "// " + NotChecked("ExternalLib.Widget", "Shine") + "\n" +
                "// Shop.Gauge.Shine is a property in A and an extension method in B: " +
                "public int Shine { get; } → public static int Shine(this Gauge gauge)\n\n",
                text.Replace("\r\n", "\n"));
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(rootA);
            await ILensServerFixture.DeleteWhenReleased(rootB);
        }
    }

    // A question for a method by name, as call_sites asks, passes over a property and a field of the
    // name through another library's type, as through a type of the assembly: Kettle's Hot and
    // Lantern's Wick, which C# does not call, and Lantern's Flicker, a property of a delegate type,
    // which it invokes. Keeper.Ring calls Flicker's extension method in its static form.
    [Theory]
    [InlineData(Kettle, "Hot",
        $"1 call to {KettleExtensions}.Hot(Kettle):\nExternalUser.Extended.Cook.Brew(Kettle)\n  KettleExtensions.Hot(kettle)")]
    [InlineData(Lantern, "Wick", $"1 call to {AccessExtensions}.Wick(Lantern):\n{Keeper}.Trim(Lantern)\n  AccessExtensions.Wick(lantern)")]
    [InlineData(Lantern, "Flicker",
        $"1 call to {AccessExtensions}.Flicker(Lantern):\n{Keeper}.Ring(Lantern)\n  AccessExtensions.Flicker(lantern)")]
    public async Task Analyze_CallSites_ThroughAnotherLibrarysType_PassesOverAPropertyOrFieldOfTheName(
        string typeName, string memberName, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText(_references.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = typeName,
                ["member_name"] = memberName,
                ["kind"] = "call_sites",
            }));
    }

    // decompile and analyze's other kinds ask for any member of the name, so the field or the
    // property answers, as through a type of the assembly.
    [Theory]
    [InlineData("Wick")]
    [InlineData("Flicker")]
    public async Task Decompile_ThroughAnotherLibrarysType_AFieldOrPropertyOfTheName_SaysWhereTheTypeComesFrom(string memberName)
    {
        var (isError, text) = await CallTool(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Lantern,
            ["member_name"] = memberName,
        });

        Assert.True(isError);
        Assert.Equal(
            $"Error: ArgumentException: {Lantern} is not defined in this assembly; it comes from ExternalLib, Version=1.0.0.0.",
            text);
    }

    [Fact]
    public async Task Analyze_ReadBy_ThroughAnotherLibrarysType_AFieldOfTheName_ListsItsReaders()
    {
        Assert.Equal(
            $"1 reader of {Lantern}.Wick:\n{Keeper}.Read(Lantern, ITallStand)",
            await CallToolText(_references.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = Lantern,
                ["member_name"] = "Wick",
                ["kind"] = "read_by",
            }));
    }

    // compare looks for a method by name, so a property or a field of the name does not come first
    // through another library's type, of a delegate type either, also where a base interface
    // declares it, as through a type of the assembly. Nor does a method C# does not bind for a call
    // on a value: a protected and a static method of another library's type, also ones that take
    // the extension method's parameters, and through ExternalUser's OwnShelf a private method of its
    // base class Shelf.
    [Theory]
    [InlineData(Kettle, "Hot", $"// {KettleExtensions}.Hot(Kettle) → bool [extension method on {Kettle}]")]
    [InlineData(Lantern, "Wick", $"// {AccessExtensions}.Wick(Lantern) → int [extension method on {Lantern}]")]
    [InlineData(Lantern, "Flicker", $"// {AccessExtensions}.Flicker(Lantern) → void [extension method on {Lantern}]")]
    [InlineData(Lantern, "Polish", $"// {AccessExtensions}.Polish(Lantern) → void [extension method on {Lantern}]")]
    [InlineData(Kettle, "Reset", $"// {KettleExtensions}.Reset(Kettle) → void [extension method on {Kettle}]")]
    [InlineData(Lantern, "IsLit", $"// {AccessExtensions}.IsLit(Lantern) → bool [extension method on {Lantern}]")]
    [InlineData(Lantern, "Snuff", $"// {AccessExtensions}.Snuff(Lantern) → void [extension method on {Lantern}]")]
    [InlineData(TallStand, "Height", $"// {AccessExtensions}.Height(ITallStand) → int [extension method on {TallStand}]")]
    [InlineData(OwnShelf, "Dust", $"// {ShelfExtensions}.Dust(OwnShelf) → void [extension method on {OwnShelf}]")]
    public async Task Compare_ThroughATypeWhoseMemberOfTheNameDoesNotComeFirst_ComparesTheExtensionMethod(
        string typeName, string memberName, string header)
    {
        Assert.Equal(
            header + " — bodies are identical in both assemblies.",
            await CallToolText(_references.Client, "compare", new()
            {
                ["assembly_a"] = _externalUserPath,
                ["assembly_b"] = _externalUserPath,
                ["type_name"] = typeName,
                ["member_name"] = memberName,
            }));
    }

    // A's Kettle has no member Hot and B's a property Hot, which C# does not call, so both builds
    // find the extension method, and the answer does not say that only A does.
    [Fact]
    public async Task Compare_ThroughAnotherLibrarysType_APropertyOnlyOneVersionHas_ComparesTheExtensionMethod()
    {
        string rootA = NewFolder();
        string rootB = NewFolder();
        File.Copy(_externalUserPath, Path.Combine(rootA, "ExternalUser.dll"));
        BareKettleAssembly.Write(rootA);
        File.Copy(_externalUserPath, Path.Combine(rootB, "ExternalUser.dll"));
        File.Copy(Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, "References", "ExternalLib.dll"),
            Path.Combine(rootB, "ExternalLib.dll"));
        try
        {
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-two-library-versions",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", rootA, "--allow-root", rootB],
            }));
            Assert.Equal(
                $"// {KettleExtensions}.Hot(Kettle) → bool [extension method on {Kettle}] — bodies are identical in both assemblies.",
                await CallToolText(client, "compare", new()
                {
                    ["assembly_a"] = Path.Combine(rootA, "ExternalUser.dll"),
                    ["assembly_b"] = Path.Combine(rootB, "ExternalUser.dll"),
                    ["type_name"] = Kettle,
                    ["member_name"] = "Hot",
                }));
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(rootA);
            await ILensServerFixture.DeleteWhenReleased(rootB);
        }
    }

    // Lantern's Polish is protected and Kettle's Reset static. C# binds the extension method of the
    // name for a call on a value outside a class derived from Lantern, so the questions through the
    // type answer about the extension method.
    [Theory]
    [InlineData(Lantern, "Polish", $"// {AccessExtensions}.Polish(Lantern) → void [extension method on {Lantern}]")]
    [InlineData(Kettle, "Reset", $"// {KettleExtensions}.Reset(Kettle) → void [extension method on {Kettle}]")]
    public async Task Decompile_ThroughAnotherLibrarysType_AProtectedOrStaticMemberOfTheName_GivesWayToTheExtensionMethod(
        string typeName, string memberName, string header)
    {
        string code = await CallToolText(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });

        Assert.Equal(header, code.Split('\n')[0]);
        Assert.DoesNotContain("Not checked", code);
    }

    [Theory]
    [InlineData(Lantern, "Polish", "used_by",
        $"1 user of {AccessExtensions}.Polish(Lantern) [extension method on {Lantern}]:\n{Keeper}.Shine(Lantern)")]
    [InlineData(Lantern, "Polish", "call_sites",
        $"1 call to {AccessExtensions}.Polish(Lantern):\n{Keeper}.Shine(Lantern)\n  AccessExtensions.Polish(lantern)")]
    [InlineData(Kettle, "Reset", "used_by",
        $"1 user of {KettleExtensions}.Reset(Kettle) [extension method on {Kettle}]:\nExternalUser.Extended.Cook.Brew(Kettle)")]
    [InlineData(Kettle, "Reset", "call_sites",
        $"1 call to {KettleExtensions}.Reset(Kettle):\nExternalUser.Extended.Cook.Brew(Kettle)\n  KettleExtensions.Reset(kettle)")]
    public async Task Analyze_ThroughAnotherLibrarysType_AProtectedOrStaticMemberOfTheName_GivesWayToTheExtensionMethod(
        string typeName, string memberName, string kind, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText(_references.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = typeName,
                ["member_name"] = memberName,
                ["kind"] = kind,
            }));
    }

    // A hint that fits the member and not the extension method reaches the member's users, which
    // ExternalUser's references name: Lamplighter derives from Lantern and calls Polish, and
    // Cook.Restart calls Reset by Kettle's name.
    [Theory]
    [InlineData(Lantern, "Polish", $"1 user of {Lantern}.Polish():\nExternalUser.Access.Lamplighter.Clean()")]
    [InlineData(Kettle, "Reset", "1 user of ExternalLib.Extended.Kettle.Reset():\nExternalUser.Extended.Cook.Restart()")]
    public async Task Analyze_ThroughAnotherLibrarysType_AHintThatFitsTheMember_ReachesItsUsers(
        string typeName, string memberName, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText(_references.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = typeName,
                ["member_name"] = memberName,
                ["parameter_count"] = 0,
                ["kind"] = "used_by",
            }));
    }

    // Lantern's static IsLit and its protected Snuff take a Lantern, as the extension methods of
    // their names do, receiver included, so a hint fits both. A hint names a declaration, so it
    // means the member, whose users ExternalUser's references name, with ExternalLib loaded and
    // without it: Keeper.Check calls IsLit by Lantern's name, and Lamplighter calls Snuff from a
    // class derived from Lantern. The name alone means the extension method, which Keeper.Look and
    // Keeper.Smother call.
    [Theory]
    [InlineData("IsLit", true, true, $"1 user of {Lantern}.IsLit(Lantern):\n{Keeper}.Check(Lantern)")]
    [InlineData("IsLit", false, true,
        $"1 user of {AccessExtensions}.IsLit(Lantern) [extension method on {Lantern}]:\n{Keeper}.Look(Lantern)")]
    [InlineData("Snuff", true, true, $"1 user of {Lantern}.Snuff(Lantern):\nExternalUser.Access.Lamplighter.Douse(Lantern)")]
    [InlineData("Snuff", false, true,
        $"1 user of {AccessExtensions}.Snuff(Lantern) [extension method on {Lantern}]:\n{Keeper}.Smother(Lantern)")]
    [InlineData("IsLit", true, false, $"1 user of {Lantern}.IsLit(Lantern):\n{Keeper}.Check(Lantern)")]
    [InlineData("IsLit", false, false,
        $"1 user of {AccessExtensions}.IsLit(Lantern) [extension method on {Lantern}]:\n{Keeper}.Look(Lantern)\n" +
        $"Not checked: whether {Lantern} or its base types have a member IsLit, which C# would take first; " +
        "ILens could not load ExternalLib, Version=1.0.0.0")]
    public async Task Analyze_ThroughAnotherLibrarysType_AHintThatFitsAMemberAndTheExtensionMethod_MeansTheMember(
        string memberName, bool hinted, bool loaded, string expected)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Lantern,
            ["member_name"] = memberName,
            ["kind"] = "used_by",
        };
        if (hinted)
            arguments["parameter_types"] = new[] { "Lantern" };

        Assert.Equal(expected, await CallToolText(loaded ? _references.Client : _server.Client, "analyze", arguments));
    }

    [Fact]
    public async Task Decompile_ThroughAnotherLibrarysType_AHintThatFitsAStaticMember_SaysWhereTheTypeComesFrom()
    {
        var (isError, text) = await CallTool(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Lantern,
            ["member_name"] = "IsLit",
            ["parameter_types"] = new[] { "Lantern" },
        });

        Assert.True(isError);
        Assert.Equal(
            $"Error: ArgumentException: {Lantern} is not defined in this assembly; it comes from ExternalLib, Version=1.0.0.0.",
            text);
    }

    // ITallStand inherits the property Height from its base interface IStand, which comes before
    // the extension method of its name, so decompile says where the interface comes from, while
    // call_sites, which looks for a method, lists the calls of the extension method.
    [Fact]
    public async Task ThroughAnotherLibrarysInterface_APropertyOfABaseInterface_ComesFirstWhereAnyMemberIsAskedFor()
    {
        var (isError, text) = await CallTool(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = TallStand,
            ["member_name"] = "Height",
        });
        Assert.True(isError);
        Assert.Equal(
            $"Error: ArgumentException: {TallStand} is not defined in this assembly; it comes from ExternalLib, Version=1.0.0.0.",
            text);

        Assert.Equal(
            $"1 call to {AccessExtensions}.Height(ITallStand):\n{Keeper}.Measure(ITallStand)\n  AccessExtensions.Height(stand)",
            await CallToolText(_references.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = TallStand,
                ["member_name"] = "Height",
                ["kind"] = "call_sites",
            }));
    }

    // OwnShelf, a class of ExternalUser, inherits ExternalLib's Shelf, whose Dust is private and
    // whose Sort is internal to a library that grants ExternalUser nothing. C# passes over both,
    // so the extension methods of their names answer.
    [Theory]
    [InlineData("Dust", $"// {ShelfExtensions}.Dust(OwnShelf) → void [extension method on {OwnShelf}]")]
    [InlineData("Sort", $"// {ShelfExtensions}.Sort(OwnShelf) → void [extension method on {OwnShelf}]")]
    public async Task Decompile_ThroughAClassWhoseBaseClassHasAMemberItCannotReach_FindsTheExtensionMethod(
        string memberName, string header)
    {
        string code = await CallToolText(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = OwnShelf,
            ["member_name"] = memberName,
        });

        Assert.Equal(header, code.Split('\n')[0]);
    }

    [Theory]
    [InlineData("used_by",
        $"1 user of {ShelfExtensions}.Dust(OwnShelf) [extension method on {OwnShelf}]:\n{Keeper}.Tidy(OwnShelf)")]
    [InlineData("call_sites", $"1 call to {ShelfExtensions}.Dust(OwnShelf):\n{Keeper}.Tidy(OwnShelf)\n  ShelfExtensions.Dust(shelf)")]
    public async Task Analyze_ThroughAClassWhoseBaseClassHasAMemberItCannotReach_AnswersForTheExtensionMethod(
        string kind, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText(_references.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = OwnShelf,
                ["member_name"] = "Dust",
                ["kind"] = kind,
            }));
    }

    // Where nothing else answers to the name, the error names the member: Shelf's Wipe is private
    // and has no extension method of its name, and its Stack is protected, which a class derived
    // from Shelf reaches.
    [Theory]
    [InlineData("Wipe")]
    [InlineData("Stack")]
    public async Task Decompile_ThroughAClassOfTheAssembly_AMemberOfAnotherLibrarysBaseClass_IsAnErrorThatNamesIt(string memberName)
    {
        var (isError, text) = await CallTool(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = OwnShelf,
            ["member_name"] = memberName,
        });

        Assert.True(isError);
        Assert.StartsWith(
            $"Error: ArgumentException: {OwnShelf} inherits ExternalLib.Access.Shelf.{memberName}() from a base class " +
            "this assembly does not define; ExternalLib.Access.Shelf comes from ",
            text);
    }

    // The error names a member of Shelf that OwnShelf cannot reach where that member alone answers
    // the question, also beside members of the name that OwnShelf reaches: Shelf's private Dust(),
    // which a hint for no parameters fits and the extension method Dust(OwnShelf) does not, and the
    // private setter of Shelf's Depth, whose getter is public.
    [Theory]
    [InlineData(null, "Dust", 0, "Dust()")]
    [InlineData("call_sites", "Dust", 0, "Dust()")]
    [InlineData(null, "set_Depth", null, "set_Depth(int)")]
    [InlineData("call_sites", "set_Depth", null, "set_Depth(int)")]
    public async Task ThroughAClassOfTheAssembly_AMemberItCannotReachBesideOnesItReaches_IsAnErrorThatNamesIt(
        string? kind, string memberName, int? parameterCount, string member)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = OwnShelf,
            ["member_name"] = memberName,
        };
        if (kind is not null)
            arguments["kind"] = kind;
        if (parameterCount is { } count)
            arguments["parameter_count"] = count;

        var (isError, text) = await CallTool(_references.Client, kind is null ? "decompile" : "analyze", arguments);

        Assert.True(isError);
        Assert.StartsWith(
            $"Error: ArgumentException: {OwnShelf} inherits ExternalLib.Access.Shelf.{member} from a base class " +
            "this assembly does not define; ExternalLib.Access.Shelf comes from ",
            text);
    }

    // The versions of ExternalLib that AccessLibraryAssembly writes make Gizmo's Tune and Shelf's
    // Sort internal. Where the library grants ExternalUser its internals, C# binds both before the
    // extension methods of their names: used_by through Gizmo lists Tune's users, and the lookup
    // through OwnShelf ends at Sort, a member of a base class it reaches. Where the library grants
    // nothing, the extension methods answer.
    [Theory]
    [InlineData(true, "1 user of ExternalLib.Access.Gizmo.Tune():\nExternalUser.Access.Keeper.Adjust(Gizmo)",
        $"Error: ArgumentException: {OwnShelf} inherits ExternalLib.Access.Shelf.Sort() from a base class this assembly does not define;")]
    [InlineData(false, $"No users of {AccessExtensions}.Tune(Gizmo) [extension method on ExternalLib.Access.Gizmo]",
        $"// {ShelfExtensions}.Sort(OwnShelf) → void [extension method on {OwnShelf}]")]
    public async Task ThroughAnotherLibrarysType_AnInternalMember_ComesFirstWhereTheLibraryGrantsItsInternals(
        bool grants, string tuneUsers, string sortStart)
    {
        string root = NewFolder();
        File.Copy(_externalUserPath, Path.Combine(root, "ExternalUser.dll"));
        AccessLibraryAssembly.Write(root, grants);
        try
        {
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-access-library",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", root],
            }));
            string user = Path.Combine(root, "ExternalUser.dll");

            Assert.Equal(
                tuneUsers,
                await CallToolText(client, "analyze", new()
                {
                    ["assembly"] = user,
                    ["type_name"] = "ExternalLib.Access.Gizmo",
                    ["member_name"] = "Tune",
                    ["kind"] = "used_by",
                }));
            var (_, sort) = await CallTool(client, "decompile", new()
            {
                ["assembly"] = user,
                ["type_name"] = OwnShelf,
                ["member_name"] = "Sort",
            });
            Assert.StartsWith(sortStart, sort);
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    // The version of ExternalLib that AccessLibraryAssembly writes derives ITallStand from
    // Bases.IStand, whose assembly no folder holds, so whether that interface has a member Height,
    // which C# would take first, was not read, and the answer says so.
    [Fact]
    public async Task Decompile_ThroughAnotherLibrarysInterfaceWhoseBaseInterfaceItCannotLoad_SaysWhatItDidNotRead()
    {
        string root = NewFolder();
        File.Copy(_externalUserPath, Path.Combine(root, "ExternalUser.dll"));
        AccessLibraryAssembly.Write(root, grants: false);
        try
        {
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-access-library",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", root],
            }));
            string[] lines = (await CallToolText(client, "decompile", new()
            {
                ["assembly"] = Path.Combine(root, "ExternalUser.dll"),
                ["type_name"] = TallStand,
                ["member_name"] = "Height",
            })).Split('\n');

            Assert.Equal($"// {AccessExtensions}.Height(ITallStand) → int [extension method on {TallStand}]", lines[0]);
            Assert.Equal(
                $"// Not checked: whether {TallStand} or its base types have a member Height, which C# would take first; " +
                "ILens could not load Bases, Version=1.0.0.0",
                lines[1]);
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    /// <summary>The line an answer found through a type of ExternalLib carries on the shared server.</summary>
    private static string NotChecked(string typeName, string memberName) =>
        $"Not checked: whether {typeName} or its base types have a member {memberName}, which C# would take first; " +
        "ILens could not load ExternalLib, Version=1.0.0.0";

    private static string NewFolder() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ILens-extensions-" + Guid.NewGuid().ToString("N"))).FullName;

    private Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments) =>
        CallToolText(_server.Client, toolName, arguments);

    private static async Task<string> CallToolText(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        var (isError, text) = await CallTool(client, toolName, arguments);
        Assert.True(!isError, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    private Task<(bool IsError, string Text)> CallTool(string toolName, Dictionary<string, object?> arguments) =>
        CallTool(_server.Client, toolName, arguments);

    private static async Task<(bool IsError, string Text)> CallTool(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        return (result.IsError == true,
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
    }
}
