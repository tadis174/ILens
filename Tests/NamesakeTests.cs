using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for methods that share their name with another method of their type and
/// differ in their type parameters, in their return type or in how a parameter is passed, or
/// whose parameter types share a short name. Every tool writes a generic method's type
/// parameters after its name, and a method made for type arguments with those arguments, so
/// such methods print apart, and a conversion operator with its return type. A line that would
/// read alike another line of its answer, and the member asked about where a namesake of it
/// would, is written in full. member_name reads brackets after a name as the count of type
/// parameters, and a name without them means the methods without type parameters where the name
/// has both. A hint is held as an exact fit first, and return_type selects by the return type.
/// list_members lists every member of a type and leaves out a base class's member only where a
/// class below hides it as C# does. The fixture is the namespace CallShapes.Namesakes in
/// Tests/Fixtures/CallShapes/Namesakes.cs.
/// </summary>
public sealed class NamesakeTests : IClassFixture<ILensServerFixture>
{
    private const string Namesakes = "CallShapes.Namesakes";

    private readonly ILensServerFixture _server;
    private readonly string _callShapesPath;

    public NamesakeTests(ILensServerFixture server)
    {
        _server = server;
        _callShapesPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "CallShapes.dll");
        if (!File.Exists(_callShapesPath))
            throw new FileNotFoundException(
                $"CallShapes fixture not found: {_callShapesPath}. " +
                "Tests/Fixtures/CallShapes/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    // Two members of one type are two members, whatever they share.
    [Fact]
    public async Task ListMembers_ListsEveryMethod_AlsoOneThatSharesItsNameAndParametersWithAnother()
    {
        Assert.Equal(
            $"10 members of public class {Namesakes}.Store:\n" +
            "10 methods:\n" +
            "  public Store()\n" +
            "  public T Get<T>(string key)\n" +
            "  public object Get(string key)\n" +
            "  public void Only<T>(T item)\n" +
            "  public void Put(string key)\n" +
            "  public void Put<T>(T item)\n" +
            "  public void Run()\n" +
            "  public void Run<T>()\n" +
            "  public void Take<T, U>(T item)\n" +
            "  public void Take<T>(T item)",
            await CallToolText("list_members", Question("Store")));

        Assert.Equal(
            $"3 members of public struct {Namesakes}.Amount:\n" +
            "3 methods:\n" +
            "  public static decimal op_Explicit(Amount amount)\n" +
            "  public static int op_Explicit(Amount amount)\n" +
            "  public static long op_Explicit(Amount amount)",
            await CallToolText("list_members", Question("Amount")));
    }

    // Cupboard's Find(string) and Clear<T>() hide neither of Shelf's methods of their names,
    // which differ in their count of type parameters; its override hides Shelf's Open.
    [Fact]
    public async Task ListMembers_WithInheritedMembers_LeavesOutOnlyWhatAClassBelowHides()
    {
        var inherited = Question("Cupboard");
        inherited["include_inherited"] = true;

        Assert.Equal(
            $"6 members of public class {Namesakes}.Cupboard : Shelf:\n" +
            "6 methods:\n" +
            "  public Cupboard()\n" +
            $"  public void Clear()  [from {Namesakes}.Shelf]\n" +
            "  public void Clear<T>()\n" +
            $"  public T Find<T>(string key)  [from {Namesakes}.Shelf]\n" +
            "  public object Find(string key)\n" +
            "  public override void Open()",
            await CallToolText("list_members", inherited));

        Assert.Equal(
            $"4 members of public class {Namesakes}.Cupboard : Shelf:\n" +
            "4 methods:\n" +
            "  public Cupboard()\n" +
            "  public void Clear<T>()\n" +
            "  public object Find(string key)\n" +
            "  public override void Open()",
            await CallToolText("list_members", Question("Cupboard")));
    }

    [Fact]
    public async Task FindMethods_WritesAGenericMethodsTypeParametersAfterItsName()
    {
        Assert.Equal(
            "10 methods:\n" +
            $"{Namesakes}.Store..ctor() → void\n" +
            $"{Namesakes}.Store.Get(string) → object\n" +
            $"{Namesakes}.Store.Get<T>(string) → T\n" +
            $"{Namesakes}.Store.Only<T>(T) → void\n" +
            $"{Namesakes}.Store.Put(string) → void\n" +
            $"{Namesakes}.Store.Put<T>(T) → void\n" +
            $"{Namesakes}.Store.Run() → void\n" +
            $"{Namesakes}.Store.Run<T>() → void\n" +
            $"{Namesakes}.Store.Take<T, U>(T) → void\n" +
            $"{Namesakes}.Store.Take<T>(T) → void",
            await CallToolText("find_methods", Question("Store")));
    }

    // A line that would read alike another of the answer in the short form is written in full,
    // every type with its namespace and the classes enclosing it; every other line stays short.
    [Fact]
    public async Task ListMembers_WritesDeclarationsThatWouldReadAlikeInFull()
    {
        Assert.Equal(
            $"7 members of public class {Namesakes}.Basket:\n" +
            "7 methods:\n" +
            "  public Basket()\n" +
            $"  public void Hold(System.Collections.Generic.List<{Namesakes}.Alpha.Badge> badges)\n" +
            $"  public void Hold(System.Collections.Generic.List<{Namesakes}.Beta.Badge> badges)\n" +
            $"  public void Take({Namesakes}.Alpha.Badge badge)\n" +
            $"  public void Take({Namesakes}.Bag.Entry entry)\n" +
            $"  public void Take({Namesakes}.Beta.Badge badge)\n" +
            $"  public void Take({Namesakes}.Box.Entry entry)",
            await CallToolText("list_members", Question("Basket")));

        Assert.Equal(
            $"4 members of public struct {Namesakes}.Voucher:\n" +
            "4 methods:\n" +
            $"  public static {Namesakes}.Alpha.Badge op_Explicit({Namesakes}.Voucher voucher)\n" +
            $"  public static {Namesakes}.Beta.Badge op_Explicit({Namesakes}.Voucher voucher)\n" +
            "  public static Voucher op_Implicit(int cents)\n" +
            "  public static Voucher op_Implicit(int? cents)",
            await CallToolText("list_members", Question("Voucher")));
    }

    [Fact]
    public async Task FindMethods_WritesLinesThatWouldReadAlikeInFull()
    {
        Assert.Equal(
            "7 methods:\n" +
            $"{Namesakes}.Basket..ctor() → void\n" +
            $"{Namesakes}.Basket.Hold(System.Collections.Generic.List<{Namesakes}.Alpha.Badge>) → void\n" +
            $"{Namesakes}.Basket.Hold(System.Collections.Generic.List<{Namesakes}.Beta.Badge>) → void\n" +
            $"{Namesakes}.Basket.Take({Namesakes}.Alpha.Badge) → void\n" +
            $"{Namesakes}.Basket.Take({Namesakes}.Bag.Entry) → void\n" +
            $"{Namesakes}.Basket.Take({Namesakes}.Beta.Badge) → void\n" +
            $"{Namesakes}.Basket.Take({Namesakes}.Box.Entry) → void",
            await CallToolText("find_methods", Question("Basket")));

        Assert.Equal(
            "4 methods:\n" +
            $"static {Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → {Namesakes}.Alpha.Badge\n" +
            $"static {Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → {Namesakes}.Beta.Badge\n" +
            $"static {Namesakes}.Voucher.op_Implicit(int) → Voucher\n" +
            $"static {Namesakes}.Voucher.op_Implicit(int?) → Voucher",
            await CallToolText("find_methods", Question("Voucher")));
    }

    // The fixture type, the name decompile is asked for, and the header line of the answer.
    public static TheoryData<string, string, string> Lookups => new()
    {
        // A name without brackets means the method without type parameters.
        { "Store", "Get", $"// {Namesakes}.Store.Get(string) → object" },
        { "Store", "Run", $"// {Namesakes}.Store.Run() → void" },
        // Brackets give the count, with the type parameters, type arguments or neither.
        { "Store", "Get<T>", $"// {Namesakes}.Store.Get<T>(string) → T" },
        { "Store", "Get<int>", $"// {Namesakes}.Store.Get<T>(string) → T" },
        { "Store", "Get<>", $"// {Namesakes}.Store.Get<T>(string) → T" },
        { "Store", "Run<T>", $"// {Namesakes}.Store.Run<T>() → void" },
        { "Store", "Take<T, U>", $"// {Namesakes}.Store.Take<T, U>(T) → void" },
        { "Store", "Take<,>", $"// {Namesakes}.Store.Take<T, U>(T) → void" },
        { "Store", "Take<string>", $"// {Namesakes}.Store.Take<T>(T) → void" },
        // Where the count leaves two, the names of the type parameters decide.
        { "Hamper<>", "Take<U>", $"// {Namesakes}.Hamper<>.Take<U>(T) → void" },
        { "Hamper<>", "Take<T>", $"// {Namesakes}.Hamper<>.Take<T>(T) → void" },
        // A generic method no method of its name without type parameters stands beside.
        { "Store", "Only", $"// {Namesakes}.Store.Only<T>(T) → void" },
        // The rule holds across the classes of the inheritance chain.
        { "Cupboard", "Find", $"// {Namesakes}.Cupboard.Find(string) → object" },
        { "Cupboard", "Find<T>", $"// {Namesakes}.Cupboard.Find<T>(string) → T [inherited from {Namesakes}.Shelf]" },
        { "Cupboard", "Clear", $"// {Namesakes}.Cupboard.Clear() → void [inherited from {Namesakes}.Shelf]" },
        { "Cupboard", "Clear<T>", $"// {Namesakes}.Cupboard.Clear<T>() → void" },
    };

    [Theory]
    [MemberData(nameof(Lookups))]
    public async Task Decompile_ReadsTheTypeParametersAfterAName(string typeName, string memberName, string header)
    {
        var question = Question(typeName);
        question["member_name"] = memberName;

        string text = await CallToolText("decompile", question);

        Assert.Equal(header, text.Split('\n')[0]);
    }

    // The fixture type, a name without brackets, a parameter type, and the header line. A hint
    // that both a method without type parameters and a generic one fit means the one without,
    // and a hint only a generic one fits means that one, as C# binds a call.
    public static TheoryData<string, string, string, string> Hinted => new()
    {
        { "Store", "Get", "string", $"// {Namesakes}.Store.Get(string) → object" },
        { "Store", "Put", "string", $"// {Namesakes}.Store.Put(string) → void" },
        { "Store", "Put", "T", $"// {Namesakes}.Store.Put<T>(T) → void" },
        { "Cupboard", "Find", "string", $"// {Namesakes}.Cupboard.Find(string) → object" },
    };

    [Theory]
    [MemberData(nameof(Hinted))]
    public async Task Decompile_ANameWithoutBracketsAndAHint_PrefersTheMethodWithoutTypeParameters(
        string typeName, string memberName, string parameterType, string header)
    {
        var question = Question(typeName);
        question["member_name"] = memberName;
        question["parameter_types"] = new[] { parameterType };

        string text = await CallToolText("decompile", question);

        Assert.Equal(header, text.Split('\n')[0]);
    }

    private const string NotFoundOn = "its base types, or as an extension method";

    // The fixture type, the name asked for and a parameter type, and the whole error.
    public static TheoryData<string, string, string?, string> Refusals => new()
    {
        // Methods that differ in their count of type parameters alone are told apart by the
        // brackets, which no parameter hint can do for them.
        {
            "Store", "Take", null,
            $"'Take' on {Namesakes}.Store has 2 matching overloads; write the type parameters of the one meant " +
            $"after its name, as listed: {Namesakes}.Store.Take<T>(T), {Namesakes}.Store.Take<T, U>(T)."
        },
        {
            "Store", "Take", "T",
            $"'Take' on {Namesakes}.Store has 2 matching overloads; write the type parameters of the one meant " +
            $"after its name, as listed: {Namesakes}.Store.Take<T>(T), {Namesakes}.Store.Take<T, U>(T)."
        },
        // A count no method of the name has: the error names those that are.
        {
            "Store", "Take<T, U, V>", null,
            $"Member 'Take<T, U, V>' not found on {Namesakes}.Store, {NotFoundOn}; the methods named Take are " +
            $"{Namesakes}.Store.Take<T>(T), {Namesakes}.Store.Take<T, U>(T)."
        },
        {
            "Cupboard", "Clear<T, U>", null,
            $"Member 'Clear<T, U>' not found on {Namesakes}.Cupboard, {NotFoundOn}; the methods named Clear are " +
            $"{Namesakes}.Cupboard.Clear<T>(), {Namesakes}.Shelf.Clear()."
        },
        // A hint no method of the name fits: the error names the generic method as well.
        {
            "Store", "Put", "int",
            $"No overload of 'Put' on {Namesakes}.Store matches parameter types (int). " +
            $"Available: {Namesakes}.Store.Put(string), {Namesakes}.Store.Put<T>(T)."
        },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Decompile_ANameThatSelectsNoSingleMethod_IsRefusedWithTheMethodsOfItsName(
        string typeName, string memberName, string? parameterType, string error)
    {
        var question = Question(typeName);
        question["member_name"] = memberName;
        if (parameterType is not null)
            question["parameter_types"] = new[] { parameterType };

        Assert.Equal($"Error: ArgumentException: {error}", await CallToolError("decompile", question));
    }

    // The fixture type, the name, a parameter type or none, a return type or none, and the
    // header line. A hint that one method fits exactly as ILens writes its types means that
    // method, also where the loose fit takes a namesake as well: a parameter type without a
    // keyword means a parameter passed by value, int means int and not int?, and a name without
    // type arguments the type without them. An exact fit on a base class stands before a loose
    // one on the class asked about.
    public static TheoryData<string, string, string?, string?, string> ExactFits => new()
    {
        // return_type selects among conversion operators that differ in their return type alone.
        { "Amount", "op_Explicit", null, "int", $"// {Namesakes}.Amount.op_Explicit(Amount) → int" },
        { "Amount", "op_Explicit", "Amount", "long", $"// {Namesakes}.Amount.op_Explicit(Amount) → long" },
        { "Amount", "op_Explicit", null, "System.Decimal", $"// {Namesakes}.Amount.op_Explicit(Amount) → decimal" },
        // Both methods of each pair; the first one's parameter type, held loosely, fits the second as well.
        { "Voucher", "op_Implicit", "int", null, $"// {Namesakes}.Voucher.op_Implicit(int) → Voucher" },
        { "Voucher", "op_Implicit", "Nullable<int>", null, $"// {Namesakes}.Voucher.op_Implicit(int?) → Voucher" },
        { "Scales", "Truncate", "decimal", null, $"// {Namesakes}.Scales.Truncate(decimal) → decimal" },
        { "Scales", "Truncate", "ref decimal", null, $"// {Namesakes}.Scales.Truncate(ref decimal) → void" },
        { "Scales", "Pass", "int", null, $"// {Namesakes}.Scales.Pass(int) → int" },
        { "Scales", "Pass", "out int", null, $"// {Namesakes}.Scales.Pass(out int) → int" },
        { "Scales", "Shift", "int", null, $"// {Namesakes}.Scales.Shift(int) → int" },
        { "Scales", "Shift", "Nullable<int>", null, $"// {Namesakes}.Scales.Shift(int?) → int" },
        { "Kiosk<>", "Accept", "Visitor", null, $"// {Namesakes}.Kiosk<>.Accept(Visitor) → void" },
        { "Kiosk<>", "Accept", "Visitor<T>", null, $"// {Namesakes}.Kiosk<>.Accept(Visitor<T>) → void" },
        // Across the classes of the inheritance chain.
        { "Booth", "Mark", "int", null, $"// {Namesakes}.Booth.Mark(int) → void [inherited from {Namesakes}.Counter]" },
        { "Booth", "Mark", "ref int", null, $"// {Namesakes}.Booth.Mark(ref int) → void" },
        { "Booth", "Fill", "int", null, $"// {Namesakes}.Booth.Fill(int) → void [inherited from {Namesakes}.Counter]" },
        { "Booth", "Fill", "Nullable<int>", null, $"// {Namesakes}.Booth.Fill(int?) → void" },
        // A qualified name reaches one of two types of one short name. The header is written in
        // full, since a namesake of the method reads alike in the short form.
        {
            "Voucher", "op_Explicit", null, "Beta.Badge",
            $"// {Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → {Namesakes}.Beta.Badge"
        },
        { "Basket", "Take", "Beta.Badge", null, $"// {Namesakes}.Basket.Take({Namesakes}.Beta.Badge) → void" },
        { "Basket", "Take", "Bag.Entry", null, $"// {Namesakes}.Basket.Take({Namesakes}.Bag.Entry) → void" },
        {
            "Basket", "Hold", "List<Beta.Badge>", null,
            $"// {Namesakes}.Basket.Hold(System.Collections.Generic.List<{Namesakes}.Beta.Badge>) → void"
        },
    };

    [Theory]
    [MemberData(nameof(ExactFits))]
    public async Task Decompile_AHintOneMethodFitsExactly_ReachesThatMethod(
        string typeName, string memberName, string? parameterType, string? returnType, string header)
    {
        string text = await CallToolText("decompile", Question(typeName, memberName, parameterType, returnType));

        Assert.Equal(header, text.Split('\n')[0]);
    }

    // The fixture type, the name, a parameter type or none, a return type or none, and the
    // whole error. Each error says what tells the candidates apart, and writes them in full
    // where they read alike in the short form.
    public static TheoryData<string, string, string?, string?, string> Undecided => new()
    {
        {
            "Amount", "op_Explicit", null, null,
            $"'op_Explicit' on {Namesakes}.Amount has 3 matching overloads; they differ in their return type alone, " +
            $"which return_type selects: {Namesakes}.Amount.op_Explicit(Amount) → int, " +
            $"{Namesakes}.Amount.op_Explicit(Amount) → long, {Namesakes}.Amount.op_Explicit(Amount) → decimal."
        },
        {
            "Voucher", "op_Explicit", null, null,
            $"'op_Explicit' on {Namesakes}.Voucher has 2 matching overloads; they differ in their return type alone, " +
            $"which return_type selects: {Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → {Namesakes}.Alpha.Badge, " +
            $"{Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → {Namesakes}.Beta.Badge."
        },
        {
            "Voucher", "op_Explicit", null, "Badge",
            $"'op_Explicit' on {Namesakes}.Voucher has 2 matching overloads; this return type fits more than one; " +
            $"write that of the one meant as listed: {Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → " +
            $"{Namesakes}.Alpha.Badge, {Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → {Namesakes}.Beta.Badge."
        },
        {
            "Basket", "Take", "Badge", null,
            $"'Take' on {Namesakes}.Basket has 2 matching overloads; these parameter types fit more than one; " +
            $"write those of the one meant as listed: {Namesakes}.Basket.Take({Namesakes}.Alpha.Badge), " +
            $"{Namesakes}.Basket.Take({Namesakes}.Beta.Badge)."
        },
        {
            "Basket", "Hold", "List<Badge>", null,
            $"'Hold' on {Namesakes}.Basket has 2 matching overloads; these parameter types fit more than one; " +
            "write those of the one meant as listed: " +
            $"{Namesakes}.Basket.Hold(System.Collections.Generic.List<{Namesakes}.Alpha.Badge>), " +
            $"{Namesakes}.Basket.Hold(System.Collections.Generic.List<{Namesakes}.Beta.Badge>)."
        },
        // A method's type parameter that hides its class's prints as the class's would.
        {
            "Hamper<>", "Take", "T", null,
            $"'Take' on {Namesakes}.Hamper<> has 2 matching overloads; write the type parameters of the one meant " +
            $"after its name, as listed: {Namesakes}.Hamper<>.Take<U>(T), {Namesakes}.Hamper<>.Take<T>(T)."
        },
    };

    [Theory]
    [MemberData(nameof(Undecided))]
    public async Task Decompile_AHintSeveralMethodsFit_IsRefusedWithWhatTellsThemApart(
        string typeName, string memberName, string? parameterType, string? returnType, string error)
    {
        Assert.Equal(
            $"Error: ArgumentException: {error}",
            await CallToolError("decompile", Question(typeName, memberName, parameterType, returnType)));
    }

    // Each method of a pair has its own calls.
    [Theory]
    [InlineData("Get", "1 call to " + Namesakes + ".Store.Get(string):\n" + Namesakes + ".Customer.Use(Store)\n  store.Get(\"b\")")]
    [InlineData("Get<T>", "1 call to " + Namesakes + ".Store.Get<T>(string):\n" + Namesakes + ".Customer.Use(Store)\n  store.Get<int>(\"a\")")]
    [InlineData("Run<T>", "1 call to " + Namesakes + ".Store.Run<T>():\n" + Namesakes + ".Customer.Use(Store)\n  store.Run<List<int>>()")]
    [InlineData("Take<T>", "1 call to " + Namesakes + ".Store.Take<T>(T):\n" + Namesakes + ".Customer.Use(Store)\n  store.Take(1)")]
    [InlineData("Take<T, U>", "1 call to " + Namesakes + ".Store.Take<T, U>(T):\n" + Namesakes + ".Customer.Use(Store)\n  store.Take<string, int>(\"c\")")]
    public async Task CallSites_ReadsTheTypeParametersAfterAName(string memberName, string expected)
    {
        Assert.Equal(expected, await Analyze($"{Namesakes}.Store", memberName, "call_sites"));
    }

    [Fact]
    public async Task UsedBy_OnAGenericMethod_NamesItWithItsTypeParameters()
    {
        Assert.Equal(
            $"1 user of {Namesakes}.Store.Get<T>(string) [declared]:\n{Namesakes}.Customer.Use(Store)",
            await Analyze($"{Namesakes}.Store", "Get<T>", "used_by"));
    }

    // A line names the method with the type arguments the code makes it for, written as
    // parameter types are, so the pairs no longer print alike and merge.
    [Fact]
    public async Task Uses_NamesEachGenericMethodWithTheTypeArgumentsItIsMadeFor()
    {
        Assert.Equal(
            $"7 symbols used by {Namesakes}.Customer.Use(Store) [declared]:\n" +
            $"{Namesakes}.Store.Get(string)\n" +
            $"{Namesakes}.Store.Get<int>(string)\n" +
            $"{Namesakes}.Store.Only<int>(int)\n" +
            $"{Namesakes}.Store.Run()\n" +
            $"{Namesakes}.Store.Run<List<int>>()\n" +
            $"{Namesakes}.Store.Take<int>(int)\n" +
            $"{Namesakes}.Store.Take<string, int>(string)",
            await Analyze($"{Namesakes}.Customer", "Use", "uses"));

        Assert.Equal(
            $"1 symbol used by {Namesakes}.Customer.CreateGeneric() [declared]:\n" +
            "System.Activator.CreateInstance<Store>()",
            await Analyze($"{Namesakes}.Customer", "CreateGeneric", "uses"));
    }

    // The assembly knows Activator from its references alone. A generic method is written with
    // its type parameters unbound, since their names are in the other assembly, and the name is
    // read as for a method the assembly defines.
    [Theory]
    [InlineData("CreateInstance", "1 user of System.Activator.CreateInstance(Type):\n" + Namesakes + ".Customer.Create()")]
    [InlineData("CreateInstance<T>", "1 user of System.Activator.CreateInstance<>():\n" + Namesakes + ".Customer.CreateGeneric()")]
    [InlineData("CreateInstance<>", "1 user of System.Activator.CreateInstance<>():\n" + Namesakes + ".Customer.CreateGeneric()")]
    public async Task UsedBy_OnAMethodOfAnotherAssemblysClass_ReadsTheTypeParametersAfterAName(string memberName, string expected)
    {
        Assert.Equal(expected, await Analyze("System.Activator", memberName, "used_by"));
    }

    // A hint only the generic method fits means that one there as well.
    [Theory]
    [InlineData(1, "1 user of System.Activator.CreateInstance(Type):\n" + Namesakes + ".Customer.Create()")]
    [InlineData(0, "1 user of System.Activator.CreateInstance<>():\n" + Namesakes + ".Customer.CreateGeneric()")]
    public async Task UsedBy_OnAMethodOfAnotherAssemblysClass_WithAHint_PrefersTheMethodWithoutTypeParameters(
        int parameterCount, string expected)
    {
        Assert.Equal(expected, await CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "System.Activator",
            ["member_name"] = "CreateInstance",
            ["parameter_count"] = parameterCount,
            ["kind"] = "used_by",
        }));
    }

    // return_type selects one conversion operator in analyze as well. The header names the one
    // asked about in full where a namesake of it reads alike in the short form.
    [Theory]
    [InlineData("Amount", "long", "used_by",
        "1 user of " + Namesakes + ".Amount.op_Explicit(Amount) → long [declared]:\n" + Namesakes + ".Cashier.WideAmount(Amount)")]
    [InlineData("Amount", "int", "call_sites",
        "1 call to " + Namesakes + ".Amount.op_Explicit(Amount) → int:\n" +
        Namesakes + ".Cashier.WholeAmount(Amount)\n  (int)amount")]
    [InlineData("Voucher", "Beta.Badge", "call_sites",
        "1 call to " + Namesakes + ".Voucher.op_Explicit(" + Namesakes + ".Voucher) → " + Namesakes + ".Beta.Badge:\n" +
        Namesakes + ".Cashier.Redeem(Voucher, List<object>)\n  (" + Namesakes + ".Beta.Badge)voucher")]
    public async Task Analyze_ReturnTypeSelectsAConversionOperator(string typeName, string returnType, string kind, string expected)
    {
        var question = Question(typeName, "op_Explicit", null, returnType);
        question["kind"] = kind;

        Assert.Equal(expected, await CallToolText("analyze", question));
    }

    [Fact]
    public async Task UsedBy_NamesTheMemberAskedAboutInFull_WhereANamesakeReadsAlike()
    {
        var question = Question("Basket", "Take", "Alpha.Badge", null);
        question["kind"] = "used_by";

        Assert.Equal(
            $"1 user of {Namesakes}.Basket.Take({Namesakes}.Alpha.Badge) [declared]:\n{Namesakes}.Cashier.Sort(Basket)",
            await CallToolText("analyze", question));
    }

    // The assembly knows System.Decimal from its references alone, whose rows give each
    // conversion operator's return type.
    [Theory]
    [InlineData("int", "1 user of System.Decimal.op_Explicit(decimal) → int:\n" + Namesakes + ".Cashier.WholeTotal(decimal)")]
    [InlineData("long", "1 user of System.Decimal.op_Explicit(decimal) → long:\n" + Namesakes + ".Cashier.WideTotal(decimal)")]
    public async Task UsedBy_OnAConversionOperatorOfAnotherAssemblysClass_ReturnTypeSelectsIt(string returnType, string expected)
    {
        Assert.Equal(expected, await CallToolText("analyze", OnDecimal(returnType)));
    }

    [Fact]
    public async Task UsedBy_OnConversionOperatorsOfAnotherAssemblysClass_WithoutAReturnType_SaysThatOneSelects()
    {
        Assert.Equal(
            "Error: ArgumentException: 'op_Explicit' on System.Decimal has 2 matching overloads; they differ in " +
            "their return type alone, which return_type selects: System.Decimal.op_Explicit(decimal) → int, " +
            "System.Decimal.op_Explicit(decimal) → long.",
            await CallToolError("analyze", OnDecimal(null)));
    }

    // Lines of one answer that would read alike in the short form are written in full: a
    // declaring type's type arguments, a parameter type and a conversion operator's return type.
    [Fact]
    public async Task Uses_WritesLinesThatWouldReadAlikeInFull()
    {
        Assert.Equal(
            $"4 symbols used by {Namesakes}.Cashier.Stock(Basket) [declared]:\n" +
            $"{Namesakes}.Basket.Hold(System.Collections.Generic.List<{Namesakes}.Alpha.Badge>)\n" +
            $"{Namesakes}.Basket.Hold(System.Collections.Generic.List<{Namesakes}.Beta.Badge>)\n" +
            $"System.Collections.Generic.List<{Namesakes}.Alpha.Badge>..ctor()\n" +
            $"System.Collections.Generic.List<{Namesakes}.Beta.Badge>..ctor()",
            await Analyze($"{Namesakes}.Cashier", "Stock", "uses"));

        Assert.Equal(
            $"3 symbols used by {Namesakes}.Cashier.Redeem(Voucher, List<object>) [declared]:\n" +
            $"{Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → {Namesakes}.Alpha.Badge\n" +
            $"{Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → {Namesakes}.Beta.Badge\n" +
            "System.Collections.Generic.List<object>.Add(object)",
            await Analyze($"{Namesakes}.Cashier", "Redeem", "uses"));
    }

    // search_code names a member in full where another member of the type would read alike.
    [Fact]
    public async Task SearchCode_NamesMembersThatWouldReadAlikeInFull()
    {
        Assert.Equal(
            $"4 lines matching operator in {Namesakes}.Voucher:\n" +
            $"{Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → {Namesakes}.Alpha.Badge\n" +
            $"  public static explicit operator {Namesakes}.Alpha.Badge(Voucher voucher)\n" +
            $"{Namesakes}.Voucher.op_Explicit({Namesakes}.Voucher) → {Namesakes}.Beta.Badge\n" +
            $"  public static explicit operator {Namesakes}.Beta.Badge(Voucher voucher)\n" +
            $"{Namesakes}.Voucher.op_Implicit(int) → Voucher\n" +
            "  public static implicit operator Voucher(int cents)\n" +
            $"{Namesakes}.Voucher.op_Implicit(int?) → Voucher\n" +
            "  public static implicit operator Voucher(int? cents)",
            await CallToolText("search_code", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = $"{Namesakes}.Voucher",
                ["regex"] = "operator",
            }));
    }

    [Theory]
    [InlineData("Get", $"// {Namesakes}.Store.Get(string) → object")]
    [InlineData("Get<T>", $"// {Namesakes}.Store.Get<T>(string) → T")]
    public async Task Compare_ReadsTheTypeParametersAfterAName(string memberName, string reference)
    {
        Assert.Equal(
            $"{reference} — bodies are identical in both assemblies.",
            await CallToolText("compare", new()
            {
                ["assembly_a"] = _callShapesPath,
                ["assembly_b"] = _callShapesPath,
                ["type_name"] = $"{Namesakes}.Store",
                ["member_name"] = memberName,
            }));
    }

    // The member view takes return_type, and names the method in full where a namesake of it
    // reads alike in the short form.
    [Theory]
    [InlineData("Amount", "op_Explicit", null, "int", $"// {Namesakes}.Amount.op_Explicit(Amount) → int")]
    [InlineData("Basket", "Take", "Beta.Badge", null, $"// {Namesakes}.Basket.Take({Namesakes}.Beta.Badge) → void")]
    public async Task Compare_SelectsByTheHintAndNamesTheMethodInFullWhereANamesakeReadsAlike(
        string typeName, string memberName, string? parameterType, string? returnType, string reference)
    {
        var question = Question(typeName, memberName, parameterType, returnType);
        question.Remove("assembly");
        question["assembly_a"] = _callShapesPath;
        question["assembly_b"] = _callShapesPath;

        Assert.Equal(
            $"{reference} — bodies are identical in both assemblies.",
            await CallToolText("compare", question));
    }

    private Dictionary<string, object?> Question(string typeName) => new()
    {
        ["assembly"] = _callShapesPath,
        ["type_name"] = $"{Namesakes}.{typeName}",
    };

    /// <summary>A question about a member of a fixture type, with each part of a hint that is given.</summary>
    private Dictionary<string, object?> Question(string typeName, string memberName, string? parameterType, string? returnType)
    {
        var question = Question(typeName);
        question["member_name"] = memberName;
        if (parameterType is not null)
            question["parameter_types"] = new[] { parameterType };
        if (returnType is not null)
            question["return_type"] = returnType;
        return question;
    }

    /// <summary>A question about who uses System.Decimal's op_Explicit, with a return type or without.</summary>
    private Dictionary<string, object?> OnDecimal(string? returnType)
    {
        var question = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "System.Decimal",
            ["member_name"] = "op_Explicit",
            ["kind"] = "used_by",
        };
        if (returnType is not null)
            question["return_type"] = returnType;
        return question;
    }

    private Task<string> Analyze(string typeName, string memberName, string kind) =>
        CallToolText("analyze", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        });

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    private async Task<string> CallToolError(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError, $"Expected an error from '{toolName}', got: {text}");
        return text;
    }
}
