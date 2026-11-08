using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for decompile of one accessor, which reads as its part of the declaration of
/// the property or event that owns it. InterfaceShapes' Language.Gauge.Level uses C# 14's field
/// keyword, and Language.Meter.Reading is a readonly property of a struct with a getter alone.
/// BaseShift's Sides.Panel.Caption is an auto-property, Panel.Closed a field-like event, and
/// Gauge.Limit a property whose accessors have bodies of their own. BaseShift2's
/// Declarations.Accessors holds an auto-property with an internal setter and one with an init
/// accessor, and its Kinds.Till.Open is a property written with =>. The assembly
/// <see cref="ExternAccessorAssembly"/> writes holds accessors without a body.
/// </summary>
public sealed class AccessorTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;

    public AccessorTests(ILensServerFixture server)
    {
        _server = server;
    }

    // An accessor of a property that uses the field keyword writes the keyword, as the property
    // does, rather than the backing field's name, and so does one of an auto-property, which
    // reads as the property writes it. An accessor with a body of its own keeps it, a struct
    // property's readonly stays on its getter, and the getter of an abstract or an interface's
    // property has no body and is not extern.
    [Theory]
    [InlineData("BaseShift.dll", "BaseShift.Gauge", "get_Limit", "get\n{\n\treturn _value;\n}\n")]
    [InlineData("InterfaceShapes.dll", "InterfaceShapes.Language.Gauge", "get_Level", "get\n{\n\treturn field;\n}\n")]
    [InlineData("InterfaceShapes.dll", "InterfaceShapes.Language.Gauge", "set_Level", "set\n{\n\tfield = Math.Max(0, value);\n}\n")]
    [InlineData("BaseShift.dll", "BaseShift.Sides.Panel", "get_Caption", "get;\n")]
    [InlineData("BaseShift.dll", "BaseShift.Sides.Panel", "set_Caption", "set;\n")]
    [InlineData("BaseShift2.dll", "BaseShift.Declarations.Accessors", "set_Size", "internal set;\n")]
    [InlineData("BaseShift2.dll", "BaseShift.Declarations.Accessors", "set_Name", "init;\n")]
    [InlineData("InterfaceShapes.dll", "InterfaceShapes.Language.Meter", "get_Reading", "readonly get\n{\n\treturn _reading;\n}\n")]
    [InlineData("InterfaceShapes.dll", "InterfaceShapes.Control", "get_PlainValue", "\nget;\n")]
    [InlineData("InterfaceShapes.dll", "InterfaceShapes.IEvery<TSelf>", "get_PlainValue", "\nget;\n")]
    public async Task AnAccessor_ReadsAsItsPartOfTheProperty(string assembly, string typeName, string memberName, string code)
    {
        string text = await Decompile(assembly, typeName, memberName);

        Assert.EndsWith(code, text);
        Assert.DoesNotContain("k__BackingField", text);
    }

    // An accessor whose property or event writes no accessor of its kind, as a field-like event
    // and a property written with => do, comes on its own, as before.
    [Theory]
    [InlineData("BaseShift.dll", "BaseShift.Sides.Panel", "add_Closed", "add\n{\n")]
    [InlineData("BaseShift2.dll", "BaseShift.Kinds.Till", "get_Open", "get\n{\n\treturn true;\n}\n")]
    public async Task AnAccessorTheDeclarationDoesNotWrite_ComesOnItsOwn(string assembly, string typeName, string memberName, string code)
    {
        string text = await Decompile(assembly, typeName, memberName);

        Assert.Contains(code, text);
    }

    // An accessor without a body, such as an engine's internal call, is extern, as a method
    // decompiled alone is: both accessors of a property without bodies, and the bodyless getter
    // of a property whose setter keeps its body. An event without bodies is written as a
    // field-like extern event, so its accessors come on their own, extern as well.
    [Theory]
    [InlineData("get_Enabled", "[MethodImpl(MethodImplOptions.InternalCall)]\nextern get;\n")]
    [InlineData("set_Enabled", "[MethodImpl(MethodImplOptions.InternalCall)]\nextern set;\n")]
    [InlineData("get_Level", "[MethodImpl(MethodImplOptions.InternalCall)]\nextern get;\n")]
    [InlineData("set_Level", "set\n{\n\t_level = value;\n}\n")]
    [InlineData("add_Changed", "[MethodImpl(MethodImplOptions.InternalCall)]\nextern add;\n")]
    [InlineData("remove_Changed", "[MethodImpl(MethodImplOptions.InternalCall)]\nextern remove;\n")]
    public async Task AnAccessorWithoutABody_IsExtern(string memberName, string code)
    {
        var path = ExternAccessorAssembly.Write();
        try
        {
            Assert.EndsWith(code, await DecompileAt(path, "Natives.Engine", memberName));
        }
        finally
        {
            ExternAccessorAssembly.TryDelete(path);
        }
    }

    private Task<string> Decompile(string assembly, string typeName, string memberName) =>
        DecompileAt(FixturePath(assembly), typeName, memberName);

    private async Task<string> DecompileAt(string path, string typeName, string memberName)
    {
        CallToolResult result = await _server.Client.CallToolAsync("decompile", new Dictionary<string, object?>
        {
            ["assembly"] = path,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.False(result.IsError == true, $"decompile returned an error: {text}");
        return text;
    }

    /// <summary>
    /// The path of a fixture assembly, which Tests.csproj's ProjectReference puts beside the
    /// shared inspection target.
    /// </summary>
    private string FixturePath(string fileName)
    {
        string path = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture not found: {path}. Its project under Tests/Fixtures/ is expected to land here via " +
                "Tests.csproj's ProjectReference.");
        return path;
    }
}
