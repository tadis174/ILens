using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace Tests;

/// <summary>
/// Writes an assembly named Polishes that forwards ExternalLib's class Kettle to ExternalLib and
/// declares, in the static class Polishes.Polishing, extension methods code can call on Kettle
/// without naming it: Polish for any type, Count for an array of any type, and Pour for any type,
/// named like Kettle's method Pour(int). It is written rather than compiled so that no row of it
/// names Kettle but the forward.
/// </summary>
internal static class ForwardedExtensionAssembly
{
    // TypeAttributes has no name for the flag that marks an exported-type row as a forward.
    private const TypeAttributes Forward = (TypeAttributes)0x00200000;

    /// <summary>Write Polishes.dll into <paramref name="folder"/> and return its path.</summary>
    public static string Write(string folder)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("Polishes") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("Polishes");
        var extension = new CustomAttributeBuilder(typeof(ExtensionAttribute).GetConstructor(Type.EmptyTypes)!, []);
        var polishing = module.DefineType("Polishes.Polishing",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
        polishing.SetCustomAttribute(extension);
        DefineExtension(polishing, extension, "Polish", typeof(void), item => item);
        DefineExtension(polishing, extension, "Count", typeof(int), item => item.MakeArrayType());
        DefineExtension(polishing, extension, "Pour", typeof(void), item => item, typeof(string));
        polishing.CreateType();

        var metadata = assembly.GenerateMetadata(out var ilStream, out var fieldData);
        var library = metadata.AddAssemblyReference(
            metadata.GetOrAddString("ExternalLib"), new Version(1, 0, 0, 0), default, default, 0, default);
        metadata.AddExportedType(Forward, metadata.GetOrAddString("ExternalLib.Extended"),
            metadata.GetOrAddString("Kettle"), library, 0);

        var image = new BlobBuilder();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), ilStream, fieldData)
            .Serialize(image);
        string path = Path.Combine(folder, "Polishes.dll");
        using var file = File.Create(path);
        image.WriteContentTo(file);
        return path;
    }

    /// <summary>
    /// Define the extension method <paramref name="name"/> with one type parameter T without
    /// constraints, whose receiver <paramref name="receiver"/> makes from T, with the further
    /// parameters <paramref name="more"/>, and a body that returns zero or nothing.
    /// </summary>
    private static void DefineExtension(TypeBuilder type, CustomAttributeBuilder extension, string name, Type returnType,
        Func<Type, Type> receiver, params Type[] more)
    {
        var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
        var item = method.DefineGenericParameters("T")[0];
        method.SetReturnType(returnType);
        method.SetParameters([receiver(item), .. more]);
        method.DefineParameter(1, ParameterAttributes.None, "item");
        method.SetCustomAttribute(extension);
        var il = method.GetILGenerator();
        if (returnType != typeof(void))
            il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }
}
