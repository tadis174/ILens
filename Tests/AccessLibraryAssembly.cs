using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Tests;

/// <summary>
/// Writes an assembly named ExternalLib for the tests on which member of one of its types comes
/// before an extension method of ExternalUser: its classes ExternalLib.Access.Gizmo and Shelf
/// declare the methods Tune and Sort as internal, with InternalsVisibleTo naming ExternalUser or
/// without it, and its interface ExternalLib.Access.ITallStand derives from Bases.IStand, an
/// interface of an assembly Bases that is not written. It stands for a version of the library
/// other than the one ExternalUser was compiled against, whose Gizmo.Tune is public, which grants
/// nothing and whose ITallStand derives from its own IStand. A fixture project cannot stand in
/// for it, since it would have to build a second ExternalLib.dll.
/// </summary>
internal static class AccessLibraryAssembly
{
    /// <summary>
    /// Write the assembly as ExternalLib.dll into <paramref name="folder"/>, granting ExternalUser
    /// the library's internals where <paramref name="grants"/> is true.
    /// </summary>
    public static void Write(string folder, bool grants)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("ExternalLib") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        if (grants)
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(InternalsVisibleToAttribute).GetConstructor([typeof(string)])!, ["ExternalUser"]));
        var module = assembly.DefineDynamicModule("ExternalLib");
        DefineClass(module, "ExternalLib.Access.Gizmo", "Tune");
        DefineClass(module, "ExternalLib.Access.Shelf", "Sort");
        module.DefineType("ExternalLib.Access.ITallStand",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract, null, [BaseInterface()]).CreateType();
        assembly.Save(Path.Combine(folder, "ExternalLib.dll"));
    }

    /// <summary>
    /// Define the public class <paramref name="name"/> with a constructor and the internal method
    /// <paramref name="method"/>, which takes nothing and does nothing.
    /// </summary>
    private static void DefineClass(ModuleBuilder module, string name, string method)
    {
        var type = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Class);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        type.DefineMethod(method, MethodAttributes.Assembly | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes)
            .GetILGenerator().Emit(OpCodes.Ret);
        type.CreateType();
    }

    /// <summary>
    /// The interface Bases.IStand of an assembly Bases, version 1.0.0.0, that is built in memory
    /// only, so a type that derives from it names an assembly no folder holds.
    /// </summary>
    private static Type BaseInterface() =>
        AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("Bases") { Version = new Version(1, 0, 0, 0) }, AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Bases")
            .DefineType("Bases.IStand", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract)
            .CreateType();
}
