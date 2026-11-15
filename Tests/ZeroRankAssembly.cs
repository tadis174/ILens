using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Tests;

/// <summary>
/// Writes an assembly whose code names an array of rank 0 in both places where code names an
/// array type. No compiler writes such an array, but a tool that guards an assembly against
/// decompiling can. ZeroRank.User.Use calls Fill of the class ZeroRank.Grid in the library
/// ZeroRankLib, which is not written, through a member reference whose signature takes such an
/// array, and then Helper. ZeroRank.User.Make calls Helper, then creates such an array and reads
/// it through the array's constructor and Get, whose class is a type specification of such an
/// array. The class also declares methods named like those the two rows name: Fill(int), which
/// Pour calls, and Get(int), which Peek calls. The signatures are written as they stand, so
/// nothing is changed after the assembly is saved.
/// </summary>
internal static class ZeroRankAssembly
{
    /// <summary>Write the assembly to a new file in the test output and return its path.</summary>
    public static string Write()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"ZeroRank_{Guid.NewGuid():N}.dll");
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("ZeroRank.dll"), metadata.GetOrAddGuid(Guid.NewGuid()),
            default, default);
        metadata.AddAssembly(metadata.GetOrAddString("ZeroRank"), new Version(1, 0, 0, 0), default, default, 0,
            AssemblyHashAlgorithm.None);

        var core = typeof(object).Assembly.GetName();
        var objectType = metadata.AddTypeReference(
            metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
                metadata.GetOrAddBlob(core.GetPublicKeyToken()!), default, default),
            metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        var grid = metadata.AddTypeReference(
            metadata.AddAssemblyReference(metadata.GetOrAddString("ZeroRankLib"), new Version(1, 0, 0, 0), default,
                default, default, default),
            metadata.GetOrAddString("ZeroRank"), metadata.GetOrAddString("Grid"));

        // The signatures in their encoded form: 0x00 a static method, 0x20 an instance method,
        // then the count of parameters, the return type and each parameter's type, where 0x01 is
        // void, 0x08 int, and 0x14 0x08 0x00 0x00 0x00 an array of int with rank 0, no sizes and
        // no lower bounds.
        var fill = metadata.AddMemberReference(grid, metadata.GetOrAddString("Fill"),
            metadata.GetOrAddBlob(new byte[] { 0x00, 0x01, 0x01, 0x14, 0x08, 0x00, 0x00, 0x00 }));
        var array = metadata.AddTypeSpecification(metadata.GetOrAddBlob(new byte[] { 0x14, 0x08, 0x00, 0x00, 0x00 }));
        var arrayConstructor = metadata.AddMemberReference(array, metadata.GetOrAddString(".ctor"),
            metadata.GetOrAddBlob(new byte[] { 0x20, 0x02, 0x01, 0x08, 0x08 }));
        var arrayGet = metadata.AddMemberReference(array, metadata.GetOrAddString("Get"),
            metadata.GetOrAddBlob(new byte[] { 0x20, 0x02, 0x08, 0x08, 0x08 }));

        // The methods' rows, in the order they are added below.
        var helper = MetadataTokens.MethodDefinitionHandle(2);
        var ownFill = MetadataTokens.MethodDefinitionHandle(3);
        var ownGet = MetadataTokens.MethodDefinitionHandle(6);

        var code = new BlobBuilder();
        var bodies = new MethodBodyStreamEncoder(code);
        var none = MetadataTokens.ParameterHandle(1);
        Method("Use", [0x00, 0x00, 0x01], none, il =>
        {
            il.OpCode(ILOpCode.Ldnull);
            il.Call(fill);
            il.Call(helper);
        });
        Method("Helper", [0x00, 0x00, 0x01], none, _ => { });
        Method("Fill", [0x00, 0x01, 0x01, 0x08], metadata.AddParameter(ParameterAttributes.None,
            metadata.GetOrAddString("count"), 1), _ => { });
        Method("Pour", [0x00, 0x00, 0x01], MetadataTokens.ParameterHandle(2), il =>
        {
            il.LoadConstantI4(1);
            il.Call(ownFill);
        });
        Method("Make", [0x00, 0x00, 0x08], MetadataTokens.ParameterHandle(2), il =>
        {
            il.Call(helper);
            il.LoadConstantI4(2);
            il.LoadConstantI4(2);
            il.OpCode(ILOpCode.Newobj);
            il.Token(arrayConstructor);
            il.LoadConstantI4(0);
            il.LoadConstantI4(0);
            il.Call(arrayGet);
        });
        Method("Get", [0x00, 0x01, 0x08, 0x08], metadata.AddParameter(ParameterAttributes.None,
            metadata.GetOrAddString("index"), 1), il => il.LoadArgument(0));
        Method("Peek", [0x00, 0x00, 0x08], MetadataTokens.ParameterHandle(3), il =>
        {
            il.LoadConstantI4(1);
            il.Call(ownGet);
        });

        // The row every module's type table starts with, then the class, which holds every method.
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
            metadata.GetOrAddString("ZeroRank"), metadata.GetOrAddString("User"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var image = new BlobBuilder();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), code)
            .Serialize(image);
        using (var file = File.Create(path))
            image.WriteContentTo(file);
        return path;

        // A public static method whose body is what emit writes, then ret.
        void Method(string name, byte[] signature, ParameterHandle parameters, Action<InstructionEncoder> emit)
        {
            var il = new InstructionEncoder(new BlobBuilder());
            emit(il);
            il.OpCode(ILOpCode.Ret);
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodImplAttributes.IL, metadata.GetOrAddString(name), metadata.GetOrAddBlob(signature),
                bodies.AddMethodBody(il, maxStack: 3), parameters);
        }
    }

    /// <summary>Best-effort cleanup, as <see cref="BurstShapedAssembly.TryDelete"/> describes.</summary>
    public static void TryDelete(string path) => BurstShapedAssembly.TryDelete(path);
}
