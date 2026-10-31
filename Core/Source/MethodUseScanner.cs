using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace ILens;

/// <summary>
/// Finds what a method's code uses, for uses: the fields and methods its instructions name,
/// each as the code names it, with its type arguments, and the types the code names itself.
/// </summary>
/// <remarks>
/// <para>
/// A type is one the code names itself where C# source writes it at that place: in a cast, a
/// type test, an array creation, a <c>default</c> or <c>new</c> of a struct, a <c>sizeof</c>
/// and a <c>typeof</c>. A type the compiler writes into an instruction without the source
/// naming it there is not read: boxing a value, reaching into an array of structs, or a call
/// on a struct, which every <c>foreach</c> over a list makes on its enumerator. Nor are the
/// types of the locals or those in the signatures of the members used, which <c>used_by</c>
/// on a type counts and which would double an answer, mostly with <c>System.Void</c>,
/// <c>System.Boolean</c> and <c>System.Int32</c> (measured 2026-10-11 on 15,071 answers of
/// three assemblies). From a type that is built from others each named type is taken, so a
/// cast to <c>Rack&lt;Part&gt;</c> gives <c>Rack&lt;&gt;</c> and <c>Part</c>, and a type
/// parameter gives nothing.
/// </para>
/// <para>
/// In two places the compiler writes one of the instructions read here although the source
/// names no type there, and each gives a line like the others, since the compiled code does
/// not tell them apart: it creates the array a call passes to a <c>params</c> parameter,
/// which gives the element type, and it sets a nullable to null as a <c>default</c> of
/// <c>Nullable&lt;T&gt;</c>.
/// </para>
/// <para>
/// What the compiler adds to make a method asynchronous is told apart and comes back as a
/// <see cref="CompilerUse"/>: the calls on the builder that starts and completes the task,
/// the calls that ask an awaited value whether it is done and fetch its result, the
/// <c>GetAwaiter</c> call an <c>await</c> compiles to, and the awaiter's type, which the
/// compiler clears as a <c>default</c> is written. The class the compiler generates for the
/// method keeps the builder and each awaiter in a field, and for an async iterator the
/// pending result as well, so the types of those fields say whose members these are, for an
/// awaiter of any name. This holds in the methods of that class and in the method that
/// starts it, the asked method or an async lambda in it; what the class's constructor uses
/// is the compiler's as a whole, which is the base constructor in a debug build. A
/// <c>GetAwaiter</c> or <c>GetResult</c> the author writes there on an awaiter type that an
/// <c>await</c> of the same method uses is taken for the compiler's as well: the compiled
/// code shows the same calls for both. What the compiler adds without such a field stays a
/// line: the rethrow after an awaited disposal, which an <c>await foreach</c> brings.
/// </para>
/// <para>
/// A read or write of the field that holds an event's subscribers is a use of the event
/// (<see cref="CompilerGeneratedFilter.EventOf"/>): raising an event reads that field, which
/// C# source and VB's <c>RaiseEvent</c> both name by the event's name, and the field counts as
/// generated, so naming it would leave the raise out of an answer that hides what the
/// compiler generated.
/// </para>
/// <para>
/// Each of the method's parts (<see cref="AssemblyHost.MethodParts"/>) is read with its own
/// type parameters, those of its class and its own. A type parameter then resolves to the one
/// the source names, <c>List&lt;T&gt;.Add(T)</c>, and a member the method and one of its
/// lambdas both use prints alike for both, since the compiler copies the names of the type
/// parameters into the classes it generates.
/// </para>
/// <para>
/// This stands in for ILSpyX's <c>MethodUsesAnalyzer</c>, whose <c>ScanMethod</c> reads the
/// fields, the methods and the <c>typeof</c> and resolves every token with an empty generic
/// context (read in ICSharpCode.ILSpyX 9.1.0.7988). A type parameter then has no owner and
/// arrives as a number, <c>List&lt;!0&gt;.Add(!0)</c>, and a lambda's class numbers a
/// method's type parameter differently than the method does, so one member came back as two.
/// The analyzer is internal and reads all parts in one pass, so its results do not say which
/// part's numbering they carry. It also reads no other instruction that names a type, and
/// lists a type only when it finds the type's definition. An ILSpyX whose analyzer resolves
/// each part in its own context could give the member lines again; the types would still
/// come from here.
/// </para>
/// </remarks>
public static class MethodUseScanner
{
    /// <summary>
    /// What the bodies of <paramref name="parts"/>, methods of <paramref name="module"/>, use.
    /// A symbol several instructions name is listed once for each. Not thread-safe: the caller
    /// holds the host's lock.
    /// </summary>
    public static IReadOnlyList<ISymbol> Scan(MetadataModule module, IEnumerable<MethodDefinitionHandle> parts)
    {
        var used = new List<ISymbol>();
        foreach (var part in parts)
            ScanBody(module, part, used);
        return used;
    }

    private static void ScanBody(MetadataModule module, MethodDefinitionHandle handle, List<ISymbol> used)
    {
        var file = module.MetadataFile;
        var definition = file.Metadata.GetMethodDefinition(handle);
        if (!definition.HasBody())
            return;

        var method = module.GetDefinition(handle);
        var context = new GenericContext(method.DeclaringTypeDefinition?.TypeParameters, method.TypeParameters);
        var members = new List<IEntity>();
        var named = new List<IType>();

        // The machinery of the async method this part belongs to: of the generated class that
        // declares it, and of each one it starts, which it does by setting up the builder.
        var machinery = new HashSet<FullTypeName>();
        var inGeneratedClass = AddMachineryOf(method.DeclaringTypeDefinition, machinery);
        try
        {
            var reader = file.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
            while (reader.RemainingBytes > 0)
            {
                var opCode = reader.DecodeOpCode();
                if (!NamesASymbol(opCode))
                {
                    reader.SkipOperand(opCode);
                    continue;
                }
                var token = MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32());
                if (!token.IsNil)
                    Resolve(token);
            }
        }
        catch (BadImageFormatException)
        {
            // What was read before an instruction the metadata reader cannot decode stays.
        }

        // The generated class's constructor is the compiler's as a whole.
        var compilerWroteAll = inGeneratedClass && method.IsConstructor;
        foreach (var member in members)
            used.Add(compilerWroteAll || IsMachinery(member) ? new CompilerUse(member) : member);
        foreach (var type in named)
        {
            // A machinery type the code names is one the compiler clears, with everything
            // it is built from.
            var compilerWrote = compilerWroteAll || Names(type);
            var types = new NamedTypeCollector();
            type.AcceptVisitor(types);
            used.AddRange(compilerWrote ? types.Found.Select(found => new CompilerUse(found)) : types.Found);
        }

        void Resolve(EntityHandle token)
        {
            try
            {
                switch (token.Kind)
                {
                    case HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification:
                        named.Add(module.ResolveType(token, context));
                        break;
                    case HandleKind.FieldDefinition or HandleKind.MethodDefinition
                        or HandleKind.MemberReference or HandleKind.MethodSpecification:
                        if (module.ResolveEntity(token, context) is { } member)
                        {
                            members.Add(EventOrMember(member));
                            if (member is IField { Name: BuilderField } builder)
                                AddMachineryOf(builder.DeclaringTypeDefinition, machinery);
                        }
                        break;
                }
            }
            catch (BadImageFormatException)
            {
                // A token the metadata reader cannot decode names nothing.
            }
        }

        bool IsMachinery(IEntity member) => member switch
        {
            IMethod { Name: "GetAwaiter" } awaited when Names(awaited.ReturnType) => true,
            IMember declared => Names(declared.DeclaringType),
            _ => false,
        };

        bool Names(IType type) => machinery.Count > 0 && NameOf(type) is { } name && machinery.Contains(name);
    }

    /// <summary>The field an async method's generated class keeps its builder in.</summary>
    private const string BuilderField = "<>t__builder";

    /// <summary>
    /// The event whose subscribers <paramref name="member"/> holds, with the type arguments the
    /// code gives the field, or <paramref name="member"/> itself for anything else.
    /// </summary>
    private static IEntity EventOrMember(IEntity member) =>
        member is IField field && CompilerGeneratedFilter.EventOf(field) is { } evt
            ? field is SpecializedMember { Substitution: var substitution } ? evt.Specialize(substitution) : evt
            : member;

    /// <summary>
    /// Adds the types of the fields <paramref name="generatedClass"/> keeps an async method's
    /// bookkeeping in, by the names the C# compiler gives them: the builder, each awaiter
    /// (<c>&lt;&gt;u__1</c>), and for an async iterator the pending result. True when the
    /// class has a builder, which only the generated class of an async method has.
    /// </summary>
    private static bool AddMachineryOf(ITypeDefinition generatedClass, HashSet<FullTypeName> machinery)
    {
        if (generatedClass is not { Name: ['<', ..] })
            return false;

        var hasBuilder = false;
        foreach (var field in generatedClass.Fields)
        {
            var builder = field.Name == BuilderField;
            hasBuilder |= builder;
            if ((builder || field.Name.StartsWith("<>u__", StringComparison.Ordinal)
                    || field.Name == "<>v__promiseOfValueOrEnd")
                && NameOf(field.Type) is { } name)
                machinery.Add(name);
        }
        return hasBuilder;
    }

    /// <summary>
    /// The name of a named type or of the generic type a constructed type is built from, or
    /// null for any other type.
    /// </summary>
    private static FullTypeName? NameOf(IType type) =>
        ReferenceFormatter.FullTypeNameOf((type as ParameterizedType)?.GenericType ?? type);

    /// <summary>
    /// True for an instruction whose token the scan resolves: one that names a field or a
    /// method, an <c>ldtoken</c>, and those with which C# source names a type.
    /// </summary>
    private static bool NamesASymbol(ILOpCode opCode) =>
        opCode.GetOperandType() is OperandType.Field or OperandType.Method or OperandType.Tok
        || opCode is ILOpCode.Castclass or ILOpCode.Isinst or ILOpCode.Newarr or ILOpCode.Initobj
            or ILOpCode.Sizeof or ILOpCode.Unbox or ILOpCode.Unbox_any;

    /// <summary>
    /// A type the scan's type system could not resolve, as a result: one of an assembly it
    /// could not load, or one the assembly it loaded does not hold. Known by its name alone,
    /// as the members of such a type are.
    /// </summary>
    public sealed class UnresolvedType : ISymbol
    {
        public UnresolvedType(FullTypeName fullName) => FullName = fullName;

        public FullTypeName FullName { get; }

        public string Name => FullName.Name;

        public SymbolKind SymbolKind => SymbolKind.TypeDefinition;
    }

    /// <summary>
    /// What only the code the compiler wrote into an async method uses, as a result: the
    /// builder's and the awaiters' members and types. Counts as compiler-generated, so an
    /// answer lists it only when asked to, and is printed as the symbol it holds.
    /// </summary>
    public sealed class CompilerUse : ISymbol
    {
        public CompilerUse(ISymbol symbol) => Symbol = symbol;

        public ISymbol Symbol { get; }

        public string Name => Symbol.Name;

        public SymbolKind SymbolKind => Symbol.SymbolKind;
    }

    /// <summary>
    /// Collects the named types a type is built from: each type definition, and each type the
    /// type system could not resolve, which ILSpy stands an unknown type in for.
    /// </summary>
    private sealed class NamedTypeCollector : TypeVisitor
    {
        public readonly List<ISymbol> Found = [];

        public override IType VisitTypeDefinition(ITypeDefinition type)
        {
            Found.Add(type);
            return base.VisitTypeDefinition(type);
        }

        public override IType VisitOtherType(IType type)
        {
            if (type is UnknownType unknown)
                Found.Add(new UnresolvedType(unknown.FullTypeName));
            return base.VisitOtherType(type);
        }
    }
}
