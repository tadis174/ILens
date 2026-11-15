using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes two libraries, one of which references the other, and a rebuild of the referenced one,
/// for the tests on a library replaced while a server holds a library that references it.
/// Parts.dll defines the class Parts.Base, with a public constructor and the virtual method
/// Before(), and the enum Parts.Shade, whose members are Red, of value 1, and Green, of value 2;
/// its rebuild names the method Latest() and the member of value 2 Lime. Shop.dll defines the
/// class Shop.Derived, which derives from Parts.Base and whose method Pick() returns the Shade of
/// value 2, so that its decompiled code names that value by the member Parts.dll gives it. Both
/// builds of Parts.dll have one assembly name and version, as two builds of a project do.
/// </summary>
internal static class RebuiltReferenceAssembly
{
    /// <summary>
    /// Write Shop.dll and the first build of Parts.dll into <paramref name="folder"/>, where a
    /// server looks for Shop.dll's references, and return Shop.dll's path.
    /// </summary>
    public static string Write(string folder)
    {
        var (parts, @base, shade) = BuildParts(rebuilt: false);
        parts.Save(Path.Combine(folder, "Parts.dll"));

        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("Shop") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("Shop");
        var derived = module.DefineType("Shop.Derived", TypeAttributes.Public | TypeAttributes.Class, @base);
        derived.DefineDefaultConstructor(MethodAttributes.Public);
        var pick = derived.DefineMethod("Pick", MethodAttributes.Public | MethodAttributes.HideBySig, shade, Type.EmptyTypes);
        var il = pick.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ret);
        derived.CreateType();

        var path = Path.Combine(folder, "Shop.dll");
        assembly.Save(path);
        return path;
    }

    /// <summary>
    /// Write the rebuild of Parts.dll into a folder of its own below <paramref name="folder"/>,
    /// where no server looks for references, and return its path.
    /// </summary>
    public static string WriteRebuild(string folder)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "rebuild")).FullName, "Parts.dll");
        BuildParts(rebuilt: true).Assembly.Save(path);
        return path;
    }

    private static (PersistedAssemblyBuilder Assembly, Type Base, Type Shade) BuildParts(bool rebuilt)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("Parts") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("Parts");

        var @base = module.DefineType("Parts.Base", TypeAttributes.Public | TypeAttributes.Class);
        @base.DefineDefaultConstructor(MethodAttributes.Public);
        var method = @base.DefineMethod(rebuilt ? "Latest" : "Before",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(void), Type.EmptyTypes);
        method.GetILGenerator().Emit(OpCodes.Ret);
        @base.CreateType();

        var shade = module.DefineEnum("Parts.Shade", TypeAttributes.Public, typeof(int));
        shade.DefineLiteral("Red", 1);
        shade.DefineLiteral(rebuilt ? "Lime" : "Green", 2);
        shade.CreateType();

        return (assembly, @base, shade);
    }
}
