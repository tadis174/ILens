using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace Tests;

/// <summary>
/// Writes builds of an assembly named Plates for the compare tests on a build that holds no code,
/// and for the tests on a question that reads code, asked of a reference assembly.
/// A reference assembly carries ReferenceAssemblyAttribute, and the tool that writes it leaves its
/// methods without a body, with an empty one or with one that throws null; a copy whose bodies a
/// tool removed has methods without a body and without a flag that says why. No compiler writes
/// either beside a build with code under one name, so all three are written here. The class
/// Plates.Stack has a field Size its constructor sets and a method Count that returns the build's
/// number, so the two builds' bodies differ. The first build has a field Label; the second has a
/// property Label instead, and a method Peek and a property Top besides.
/// </summary>
internal static class CodelessAssembly
{
    /// <summary>What a build holds: code, the reference assembly's mark, or methods stripped of their bodies.</summary>
    public enum Kind { Code, Marked, Stripped }

    /// <summary>
    /// Write the first build, or with <paramref name="second"/> the second, of the kind given to a
    /// new file in the test output, and return its path. A stripped build's methods keep the flags
    /// of a method with code and lose the address of their body after saving.
    /// </summary>
    public static string Write(Kind kind, bool second)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"Plates_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("Plates") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        if (kind == Kind.Marked)
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(ReferenceAssemblyAttribute).GetConstructor(Type.EmptyTypes)!, []));
        var module = assembly.DefineDynamicModule("Plates");

        var stack = module.DefineType("Plates.Stack", TypeAttributes.Public | TypeAttributes.Class);
        var size = stack.DefineField("Size", typeof(int), FieldAttributes.Public);
        var constructor = stack.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard, Type.EmptyTypes);
        var code = constructor.GetILGenerator();
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Ldc_I4_1);
        code.Emit(OpCodes.Stfld, size);
        code.Emit(OpCodes.Ret);
        Returning(stack, "Count", second ? 2 : 1);
        if (second)
        {
            Returning(stack, "Peek", 3);
            Property(stack, "Top", Returning(stack, "get_Top", 4, MethodAttributes.SpecialName));
            Property(stack, "Label", Returning(stack, "get_Label", 5, MethodAttributes.SpecialName));
        }
        else
            stack.DefineField("Label", typeof(int), FieldAttributes.Public);
        stack.CreateType();

        assembly.Save(path);
        if (kind == Kind.Stripped)
            RemoveBodies(path);
        return path;
    }

    private static MethodBuilder Returning(TypeBuilder type, string name, int value, MethodAttributes attributes = 0)
    {
        var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.HideBySig | attributes,
            typeof(int), Type.EmptyTypes);
        var code = method.GetILGenerator();
        code.Emit(OpCodes.Ldc_I4, value);
        code.Emit(OpCodes.Ret);
        return method;
    }

    private static void Property(TypeBuilder type, string name, MethodBuilder getter) =>
        type.DefineProperty(name, PropertyAttributes.None, typeof(int), null).SetGetMethod(getter);

    /// <summary>
    /// Zero the address of every method's body, as a tool that strips a copy of its code leaves a
    /// method: its row keeps its flags, and the body lies in the file with nothing pointing to it.
    /// </summary>
    private static void RemoveBodies(string path)
    {
        var image = File.ReadAllBytes(path);
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            var metadata = pe.GetMetadataReader();
            var table = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.MethodDef);
            var rowSize = metadata.GetTableRowSize(TableIndex.MethodDef);
            // The address is a row's first column, four bytes long.
            for (var row = 0; row < metadata.GetTableRowCount(TableIndex.MethodDef); row++)
                Array.Clear(image, table + row * rowSize, 4);
        }
        File.WriteAllBytes(path, image);
    }

    /// <summary>Best-effort cleanup, as <see cref="BurstShapedAssembly.TryDelete"/> describes.</summary>
    public static void TryDelete(params string[] paths) => BurstShapedAssembly.TryDelete(paths);
}
