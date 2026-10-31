using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Tests;

/// <summary>
/// Writes two builds of an assembly named Gauges whose class Shop.Gauge derives from ExternalLib's
/// Widget. In the first, Gauge has a property Shine; in the second, a static class declares an
/// extension method Shine for Gauge instead. Read without ExternalLib, Gauge's base class is not
/// read, so a member of Widget of that name could be what C# binds before the extension method.
/// A fixture project cannot stand in for it, since the two builds would be two projects that build
/// one DLL of one name.
/// </summary>
internal static class KindShiftAssembly
{
    /// <summary>
    /// Write the build with the property, or with the extension method, as Gauges.dll into
    /// <paramref name="folder"/>, and return its path. <paramref name="externalLib"/> is the path of
    /// ExternalLib.dll, which the build references and which is not copied.
    /// </summary>
    public static string Write(string folder, bool withProperty, string externalLib)
    {
        Type widget = Assembly.LoadFrom(externalLib).GetType("ExternalLib.Widget", throwOnError: true)!;
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("Gauges") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("Gauges");

        var gauge = module.DefineType("Shop.Gauge", TypeAttributes.Public | TypeAttributes.Class, widget);
        gauge.DefineDefaultConstructor(MethodAttributes.Public);
        if (withProperty)
        {
            var getter = gauge.DefineMethod("get_Shine",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                typeof(int), Type.EmptyTypes);
            ReturnConstant(getter, 1);
            gauge.DefineProperty("Shine", PropertyAttributes.None, typeof(int), null).SetGetMethod(getter);
        }
        gauge.CreateType();

        if (!withProperty)
        {
            var extension = new CustomAttributeBuilder(typeof(ExtensionAttribute).GetConstructor(Type.EmptyTypes)!, []);
            var extensions = module.DefineType("Shop.GaugeExtensions",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
            extensions.SetCustomAttribute(extension);
            var shine = extensions.DefineMethod("Shine",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, typeof(int), [gauge]);
            shine.DefineParameter(1, ParameterAttributes.None, "gauge");
            shine.SetCustomAttribute(extension);
            ReturnConstant(shine, 2);
            extensions.CreateType();
        }

        string path = Path.Combine(folder, "Gauges.dll");
        assembly.Save(path);
        return path;
    }

    private static void ReturnConstant(MethodBuilder method, int value)
    {
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, value);
        il.Emit(OpCodes.Ret);
    }
}
