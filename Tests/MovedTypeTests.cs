using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for compare where one build defines a type and the other takes it from
/// another assembly, as after the type moved to a library: the other build references the type
/// there, or forwards it there. The type's line in the list of changed types, the header of the
/// type's comparison and each line of the member view that says only one build has a member name
/// that assembly with its version, and a method one build declares on the type is compared with
/// an extension method the other declares for the library's type, as two methods.
/// <para>
/// ExternalLib stands for the build that defines the types. ExternalUser, which takes them from
/// ExternalLib, stands for the other: beside ExternalLib, beside the version of it that
/// BareKettleAssembly writes, whose Kettle has neither Pour nor Hot, and with no ExternalLib to
/// load. A library that ForwardingAssembly writes under ExternalLib's name stands for a build
/// that forwards Kettle, Limits with the class nested in it, and Marker without the class nested
/// in it to ExternalCore.
/// </para>
/// </summary>
public sealed class MovedTypeTests : IClassFixture<MovedTypeTests.FoldersFixture>
{
    private const string Kettle = "ExternalLib.Extended.Kettle";
    private const string KettleExtensions = "ExternalUser.Extended.KettleExtensions";
    private const string FromLib = "ExternalLib, Version=1.0.0.0";
    private const string ToCore = "ExternalCore, Version=3.0.0.0";

    private readonly FoldersFixture _folders;

    public MovedTypeTests(FoldersFixture folders) => _folders = folders;

    /// <summary>
    /// One server on a folder whose subfolders hold ExternalLib with ExternalUser beside it,
    /// ExternalUser beside the version of ExternalLib that BareKettleAssembly writes, ExternalUser
    /// alone, and the forwarding library. The reference search looks in an assembly's own folder
    /// and at the top of the allow-root, where no file lies, so each copy of ExternalUser finds
    /// only the library beside it.
    /// </summary>
    public sealed class FoldersFixture : IAsyncLifetime
    {
        private string _root = null!;

        public McpClient Client { get; private set; } = null!;
        public string Library { get; private set; } = null!;
        public string User { get; private set; } = null!;
        public string UserBesideBareKettle { get; private set; } = null!;
        public string UserAlone { get; private set; } = null!;
        public string Forwarding { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            string testOutput = AppContext.BaseDirectory;
            string user = Path.Combine(testOutput, "ExternalUser.dll");
            _root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "ILens-moved-" + Guid.NewGuid().ToString("N"))).FullName;

            string withLibrary = Folder("library");
            Library = Copy(Path.Combine(testOutput, "References", "ExternalLib.dll"), withLibrary);
            User = Copy(user, withLibrary);
            string withBareKettle = Folder("bare-kettle");
            UserBesideBareKettle = Copy(user, withBareKettle);
            BareKettleAssembly.Write(withBareKettle);
            UserAlone = Copy(user, Folder("alone"));
            Forwarding = ForwardingAssembly.Write(Folder("forwarding"), "ExternalLib", new Version(2, 0, 0, 0),
                "ExternalCore", new Version(3, 0, 0, 0),
                ("ExternalLib.Extended", "Kettle"), ("ExternalLib", "Limits"), ("ExternalLib", "Limits+Deep"),
                ("ExternalLib.Namesakes", "Marker"));

            Client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-moved-types",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", _root],
            }));
        }

        public async Task DisposeAsync()
        {
            if (Client is not null)
                await Client.DisposeAsync();
            await ILensServerFixture.DeleteWhenReleased(_root);
        }

        private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

        private static string Copy(string file, string folder)
        {
            string copy = Path.Combine(folder, Path.GetFileName(file));
            File.Copy(file, copy);
            return copy;
        }
    }

    // TravelKettle is a class of ExternalLib that ExternalUser never names, so its line stays as it was.
    [Theory]
    [InlineData(false, $"- {Kettle}, which in B comes from {FromLib}")]
    [InlineData(true, $"+ {Kettle}, which in A comes from {FromLib}")]
    [InlineData(false, "- ExternalLib.Extended.TravelKettle")]
    public async Task Compare_ChangedTypes_ATypeTheOtherBuildReferences_NamesTheAssemblyItComesFrom(bool swapped, string line)
    {
        string text = await Compare(_folders.Library, _folders.User, swapped, new() { ["limit"] = 1000 });

        Assert.Contains(line, text.Split('\n'));
    }

    [Theory]
    [InlineData(false, $"6 member changes in {Kettle}, which only A has; in B it comes from {FromLib}:")]
    [InlineData(true, $"6 member changes in {Kettle}, which only B has; in A it comes from {FromLib}:")]
    public async Task Compare_MemberChanges_OfATypeTheOtherBuildReferences_NamesTheAssemblyItComesFrom(bool swapped, string header)
    {
        string text = await Compare(_folders.Library, _folders.User, swapped, new() { ["type_name"] = Kettle });

        Assert.Equal(header, text.Split('\n')[0]);
    }

    // Kettle in ExternalLib has Pour(int), which C# takes before ExternalUser's extension method of
    // that name, so ExternalUser, read beside ExternalLib, finds no method Pour through Kettle.
    // Gadget<T> has no Fill, and ExternalUser finds its extension method for Gadget<int> through
    // the generic type, named without its type parameters. Widget's Label is a property, which
    // compare passes over when it looks for a method, so through Widget ExternalUser finds its
    // extension method Label for every type, and the name is a property in A and an extension
    // method in B, as where both builds define the type.
    [Theory]
    [InlineData(false, "ExternalLib.Gadget", "Fill",
        "// ExternalUser.WidgetExtensions.Fill(Gadget<int>) → void [extension method on ExternalLib.Gadget<int>] — " +
        $"only B has it; in B ExternalLib.Gadget<> comes from {FromLib}.\n\n")]
    [InlineData(false, Kettle, "Pour",
        $"// {Kettle}.Pour(int) → void — only A has it; in B {Kettle} comes from {FromLib}.\n\n")]
    [InlineData(true, Kettle, "Pour",
        $"// {Kettle}.Pour(int) → void — only B has it; in A {Kettle} comes from {FromLib}.\n\n")]
    [InlineData(false, "ExternalLib.Widget", "Label",
        "// ExternalLib.Widget.Label is a property in A and an extension method in B: " +
        "public string Label { get; set; } → public static string Label<T>(this T item)\n\n")]
    [InlineData(false, "ExternalLib.Widget", "Changed",
        $"// ExternalLib.Widget.Changed — only A has it; in B ExternalLib.Widget comes from {FromLib}.\n\n" +
        "using System;\n\npublic event EventHandler Changed;\n")]
    public async Task Compare_Bodies_OfAMemberOfATypeTheOtherBuildReferences_SaysOnlyOneHasItAndWhereTheOtherTakesTheType(
        bool swapped, string typeName, string memberName, string start)
    {
        string text = await Compare(_folders.Library, _folders.User, swapped,
            new() { ["type_name"] = typeName, ["member_name"] = memberName });

        Assert.StartsWith(start, text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Compare_Bodies_OfAFieldOfATypeTheOtherBuildReferences_SaysOnlyOneHasItAndWhereTheOtherTakesTheType()
    {
        var (isError, text) = await CallTool("compare", new()
        {
            ["assembly_a"] = _folders.Library,
            ["assembly_b"] = _folders.User,
            ["type_name"] = "ExternalLib.Widget",
            ["member_name"] = "Size",
        });

        Assert.True(isError);
        Assert.Equal(
            "Error: ArgumentException: ExternalLib.Widget.Size is a field, which has no body, and only A has it; " +
            $"in B ExternalLib.Widget comes from {FromLib}; ExternalLib.Widget..ctor(int) sets it.",
            text);
    }

    // Beside the version of ExternalLib that BareKettleAssembly writes, Kettle has no member Pour or
    // Hot, so ExternalUser finds its extension methods of those names through Kettle, as where
    // Kettle stays in the assembly: Pour beside ExternalLib's Pour(int), whose body is as empty, and
    // Hot beside ExternalLib's property.
    [Theory]
    [InlineData(false,
        $"// {Kettle}.Pour(int) → void in A and {KettleExtensions}.Pour(Kettle, string) → void " +
        $"[extension method on {Kettle}] in B are different methods — bodies are identical in both assemblies.")]
    [InlineData(true,
        $"// {KettleExtensions}.Pour(Kettle, string) → void [extension method on {Kettle}] in A and " +
        $"{Kettle}.Pour(int) → void in B are different methods — bodies are identical in both assemblies.")]
    public async Task Compare_Bodies_OfAMethodTheOtherBuildDeclaresAsAnExtensionMethodForTheLibrarysType_ComparesTwoMethods(
        bool swapped, string expected)
    {
        Assert.Equal(expected, await Compare(_folders.Library, _folders.UserBesideBareKettle, swapped,
            new() { ["type_name"] = Kettle, ["member_name"] = "Pour" }));
    }

    [Fact]
    public async Task Compare_Bodies_OfAPropertyTheOtherBuildDeclaresAsAnExtensionMethodForTheLibrarysType_NamesBothKinds()
    {
        string text = await Compare(_folders.Library, _folders.UserBesideBareKettle, swapped: false,
            new() { ["type_name"] = Kettle, ["member_name"] = "Hot" });

        Assert.StartsWith(
            $"// {Kettle}.Hot is a property in A and an extension method in B: " +
            "public bool Hot { get; set; } → public static bool Hot(this Kettle kettle)\n\n" +
            $"// A: {Kettle}.Hot\npublic bool Hot {{ get; set; }}\n\n" +
            $"// B: {KettleExtensions}.Hot(Kettle) → bool [extension method on {Kettle}]\n",
            text.Replace("\r\n", "\n"));
    }

    // Without ExternalLib, ExternalUser's lookup through Kettle knows Kettle's members only from its
    // own references, which name no Boil and no Hot, so the answer says first what it did not read.
    [Fact]
    public async Task Compare_Bodies_OfAnExtensionMethodFoundThroughATypeWhoseLibraryWasNotLoaded_SaysFirstWhatItDidNotRead()
    {
        string text = await Compare(_folders.Library, _folders.UserAlone, swapped: false,
            new() { ["type_name"] = Kettle, ["member_name"] = "Boil" });

        Assert.StartsWith(
            $"// {NotChecked("Boil")}\n" +
            $"// {KettleExtensions}.Boil(Kettle) → void [extension method on {Kettle}] — only B has it; " +
            $"in B {Kettle} comes from {FromLib}.\n\n",
            text);
        Assert.Contains("public static void Boil(this Kettle kettle)", text);
    }

    [Fact]
    public async Task Compare_Bodies_OfAPropertyTheOtherBuildMayDeclareAsAnExtensionMethod_SaysFirstWhatItDidNotRead()
    {
        string text = await Compare(_folders.Library, _folders.UserAlone, swapped: false,
            new() { ["type_name"] = Kettle, ["member_name"] = "Hot" });

        Assert.StartsWith(
            $"// {NotChecked("Hot")}\n" +
            $"// {Kettle}.Hot is a property in A and an extension method in B: " +
            "public bool Hot { get; set; } → public static bool Hot(this Kettle kettle)\n\n",
            text.Replace("\r\n", "\n"));
        Assert.Contains($"// B: {KettleExtensions}.Hot(Kettle) → bool [extension method on {Kettle}]\n",
            text.Replace("\r\n", "\n"));
    }

    // Widget is a class the forwarding library neither forwards nor references, so its line stays
    // as it was. Deep is nested in Limits and forwarded with a row of its own; Scope is nested in
    // Marker and has none, and lies where Marker lies.
    [Theory]
    [InlineData(false, $"- {Kettle}, which B forwards to {ToCore}")]
    [InlineData(true, $"+ {Kettle}, which A forwards to {ToCore}")]
    [InlineData(false, $"- ExternalLib.Limits.Deep, which B forwards to {ToCore}")]
    [InlineData(false, $"- ExternalLib.Namesakes.Marker.Scope, which B forwards to {ToCore}")]
    [InlineData(false, "- ExternalLib.Widget")]
    public async Task Compare_ChangedTypes_ATypeTheOtherBuildForwards_NamesTheAssemblyItForwardsTo(bool swapped, string line)
    {
        string text = await Compare(_folders.Library, _folders.Forwarding, swapped, new() { ["limit"] = 1000 });

        Assert.Contains(line, text.Split('\n'));
    }

    [Theory]
    [InlineData(false, $"6 member changes in {Kettle}, which only A has; B forwards it to {ToCore}:")]
    [InlineData(true, $"6 member changes in {Kettle}, which only B has; A forwards it to {ToCore}:")]
    public async Task Compare_MemberChanges_OfATypeTheOtherBuildForwards_NamesTheAssemblyItForwardsTo(bool swapped, string header)
    {
        string text = await Compare(_folders.Library, _folders.Forwarding, swapped, new() { ["type_name"] = Kettle });

        Assert.Equal(header, text.Split('\n')[0]);
    }

    [Theory]
    [InlineData(false, Kettle, "Pour",
        $"// {Kettle}.Pour(int) → void — only A has it; B forwards {Kettle} to {ToCore}.\n\n")]
    [InlineData(true, Kettle, "Pour",
        $"// {Kettle}.Pour(int) → void — only B has it; A forwards {Kettle} to {ToCore}.\n\n")]
    [InlineData(false, "ExternalLib.Limits", "Most",
        $"// ExternalLib.Limits.Most = 10 — only A has it; B forwards ExternalLib.Limits to {ToCore}.")]
    public async Task Compare_Bodies_OfAMemberOfATypeTheOtherBuildForwards_SaysOnlyOneHasItAndWhereTheOtherForwardsTheType(
        bool swapped, string typeName, string memberName, string start)
    {
        string text = await Compare(_folders.Library, _folders.Forwarding, swapped,
            new() { ["type_name"] = typeName, ["member_name"] = memberName });

        Assert.StartsWith(start, text);
    }

    /// <summary>The line an answer carries that found a method through Kettle without ExternalLib.</summary>
    private static string NotChecked(string memberName) =>
        $"Not checked: whether {Kettle} or its base types have a member {memberName}, which C# would take first; " +
        $"ILens could not load {FromLib}";

    /// <summary>
    /// compare with <paramref name="defining"/>, the build that defines the types, as A, or as B
    /// where <paramref name="swapped"/>.
    /// </summary>
    private async Task<string> Compare(string defining, string other, bool swapped, Dictionary<string, object?> more)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly_a"] = swapped ? other : defining,
            ["assembly_b"] = swapped ? defining : other,
        };
        foreach (var (name, value) in more)
            arguments[name] = value;

        var (isError, text) = await CallTool("compare", arguments);
        Assert.True(!isError, $"compare returned an error: {text}");
        return text;
    }

    private async Task<(bool IsError, string Text)> CallTool(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _folders.Client.CallToolAsync(toolName, arguments);
        return (result.IsError == true,
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
    }
}
