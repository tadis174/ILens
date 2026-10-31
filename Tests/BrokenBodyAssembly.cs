using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Tests;

/// <summary>
/// Writes an assembly in which the code of one method breaks off inside an instruction that
/// names a method. A tool that guards an assembly against decompiling writes such code on
/// purpose, and a damaged file can have it. No compiler writes it, so the method's last
/// instruction is replaced after the assembly is saved. The class BrokenBody.Shapes has the method Cut, which
/// calls Plain before its code breaks off, and Plain and the virtual method Virtual, which
/// BrokenBody.User.Use calls; BrokenBody.Derived overrides Virtual.
/// </summary>
internal static class BrokenBodyAssembly
{
    /// <summary>Write the assembly to a new file in the test output and return its path.</summary>
    public static string Write()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"BrokenBody_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("BrokenBody") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("BrokenBody");

        var shapes = module.DefineType("BrokenBody.Shapes", TypeAttributes.Public | TypeAttributes.Class);
        var plain = Returning(shapes, "Plain", MethodAttributes.Public);
        var @virtual = Returning(shapes, "Virtual",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot);
        var cut = shapes.DefineMethod("Cut", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes);
        var code = cut.GetILGenerator();
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Call, plain);
        code.Emit(OpCodes.Ret);
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
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var metadata = pe.GetMetadataReader();
            var body = metadata.MethodDefinitions
                .Select(metadata.GetMethodDefinition)
                .Single(method => metadata.GetString(method.Name) == "Cut");
            var section = pe.PEHeaders.SectionHeaders[pe.PEHeaders.GetContainingSectionIndex(body.RelativeVirtualAddress)];
            var header = body.RelativeVirtualAddress - section.VirtualAddress + section.PointerToRawData;
            // A body this small has a header of one byte, which holds the size of the code above
            // the two tag bits 10. The code's last byte is its ret.
            var last = header + (image[header] >> 2);
            if ((image[header] & 3) != 2 || image[last] != 0x2A)
                throw new InvalidOperationException(
                    $"The body of Cut has no one-byte header or does not end in ret: {image[header]:x2} ... {image[last]:x2}.");
            // A call, whose four operand bytes the body does not have.
            image[last] = 0x28;
        }
        File.WriteAllBytes(path, image);
        return path;
    }

    private static MethodBuilder Returning(TypeBuilder type, string name, MethodAttributes attributes)
    {
        var method = type.DefineMethod(name, attributes | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes);
        method.GetILGenerator().Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>Best-effort cleanup, as <see cref="BurstShapedAssembly.TryDelete"/> describes.</summary>
    public static void TryDelete(string path) => BurstShapedAssembly.TryDelete(path);
}
