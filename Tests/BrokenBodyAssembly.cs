using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Tests;

/// <summary>
/// Writes an assembly in which the code of some methods breaks off inside an instruction. A tool
/// that guards an assembly against decompiling writes such code on purpose, and a damaged file
/// can have it. No compiler writes it, so the last instruction of each such method, its ret, is
/// replaced after the assembly is saved by a call without the four bytes of its operand. The
/// class BrokenBody.Shapes has the methods Cut, which calls Plain before its code breaks off, and
/// CutAddress, which passes the address of the field Count to the static method Touch, which
/// writes through it, before its code breaks off; Plain and the virtual method Virtual, which
/// BrokenBody.User.Use calls; BrokenBody.Derived overrides Virtual. <see cref="CutCopy"/> breaks
/// off a method of a copy of another assembly the same way.
/// </summary>
internal static class BrokenBodyAssembly
{
    /// <summary>
    /// Write the assembly to a new file in the test output and return its path.
    /// <paramref name="moreCuts"/> adds that many methods to Shapes, Cut1, Cut2 and so on, whose
    /// code is a ret alone, broken off as well, and <paramref name="cutGetter"/> adds the
    /// property Size, whose getter returns 1 before its code breaks off.
    /// </summary>
    public static string Write(int moreCuts = 0, bool cutGetter = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"BrokenBody_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("BrokenBody") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("BrokenBody");

        var shapes = module.DefineType("BrokenBody.Shapes", TypeAttributes.Public | TypeAttributes.Class);
        var count = shapes.DefineField("Count", typeof(int), FieldAttributes.Public);
        var plain = Returning(shapes, "Plain", MethodAttributes.Public);
        var @virtual = Returning(shapes, "Virtual",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot);
        var touch = shapes.DefineMethod("Touch", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            typeof(void), [typeof(int).MakeByRefType()]);
        var code = touch.GetILGenerator();
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Ldc_I4_1);
        code.Emit(OpCodes.Stind_I4);
        code.Emit(OpCodes.Ret);
        code = shapes.DefineMethod("Cut", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes)
            .GetILGenerator();
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Call, plain);
        code.Emit(OpCodes.Ret);
        code = shapes.DefineMethod("CutAddress", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes)
            .GetILGenerator();
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Ldflda, count);
        code.Emit(OpCodes.Call, touch);
        code.Emit(OpCodes.Ret);
        for (var i = 1; i <= moreCuts; i++)
            Returning(shapes, $"Cut{i}", MethodAttributes.Public);
        if (cutGetter)
        {
            var getter = shapes.DefineMethod("get_Size",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName, typeof(int), Type.EmptyTypes);
            code = getter.GetILGenerator();
            code.Emit(OpCodes.Ldc_I4_1);
            code.Emit(OpCodes.Ret);
            shapes.DefineProperty("Size", PropertyAttributes.None, typeof(int), Type.EmptyTypes).SetGetMethod(getter);
        }
        shapes.CreateType();

        var derived = module.DefineType("BrokenBody.Derived", TypeAttributes.Public | TypeAttributes.Class, shapes);
        Returning(derived, "Virtual", MethodAttributes.Public | MethodAttributes.Virtual);
        derived.CreateType();

        var user = module.DefineType("BrokenBody.User",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
        var use = user.DefineMethod("Use", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [shapes]);
        code = use.GetILGenerator();
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Callvirt, plain);
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Callvirt, @virtual);
        code.Emit(OpCodes.Ret);
        user.CreateType();
        assembly.Save(path);

        var image = File.ReadAllBytes(path);
        Cut(image, "BrokenBody.Shapes", "Cut");
        Cut(image, "BrokenBody.Shapes", "CutAddress");
        for (var i = 1; i <= moreCuts; i++)
            Cut(image, "BrokenBody.Shapes", $"Cut{i}");
        if (cutGetter)
            Cut(image, "BrokenBody.Shapes", "get_Size");
        File.WriteAllBytes(path, image);
        return path;
    }

    /// <summary>
    /// Copy <paramref name="source"/> to a new file in the test output, beside the assemblies it
    /// references, with the code of the method <paramref name="methodName"/> of the top-level
    /// type <paramref name="typeName"/> breaking off as <see cref="Write"/> breaks it off, and
    /// return the copy's path.
    /// </summary>
    public static string CutCopy(string source, string typeName, string methodName)
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            $"{Path.GetFileNameWithoutExtension(source)}_Cut_{Guid.NewGuid():N}.dll");
        var image = File.ReadAllBytes(source);
        Cut(image, typeName, methodName);
        File.WriteAllBytes(path, image);
        return path;
    }

    /// <summary>
    /// Replace the last instruction of the method, which has to be its ret, by a call opcode
    /// without the four bytes of its operand. A tiny header is one byte, which holds the size of
    /// the code above the two tag bits 10; a fat one, tagged 11, is three words, the second of
    /// which holds it.
    /// </summary>
    private static void Cut(byte[] image, string typeName, string methodName)
    {
        int last;
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var metadata = pe.GetMetadataReader();
            var body = metadata.MethodDefinitions
                .Select(metadata.GetMethodDefinition)
                .Single(method => metadata.GetString(method.Name) == methodName
                    && FullName(metadata, metadata.GetTypeDefinition(method.GetDeclaringType())) == typeName);
            var section = pe.PEHeaders.SectionHeaders[pe.PEHeaders.GetContainingSectionIndex(body.RelativeVirtualAddress)];
            var header = body.RelativeVirtualAddress - section.VirtualAddress + section.PointerToRawData;
            last = (image[header] & 3) switch
            {
                2 => header + (image[header] >> 2),
                3 => header + 12 + BitConverter.ToInt32(image, header + 4) - 1,
                _ => throw new InvalidOperationException($"The body of {typeName}.{methodName} has no header tagged 10 or 11."),
            };
        }
        if (image[last] != 0x2A)
            throw new InvalidOperationException($"The body of {typeName}.{methodName} does not end in ret: {image[last]:x2}.");
        // A call, whose four operand bytes the body does not have.
        image[last] = 0x28;
    }

    private static string FullName(MetadataReader metadata, TypeDefinition type) =>
        $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}";

    private static MethodBuilder Returning(TypeBuilder type, string name, MethodAttributes attributes)
    {
        var method = type.DefineMethod(name, attributes | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes);
        method.GetILGenerator().Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>Best-effort cleanup, as <see cref="BurstShapedAssembly.TryDelete"/> describes.</summary>
    public static void TryDelete(string path) => BurstShapedAssembly.TryDelete(path);
}
