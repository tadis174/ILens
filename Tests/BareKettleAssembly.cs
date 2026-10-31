using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly named ExternalLib whose class ExternalLib.Extended.Kettle declares nothing
/// but a constructor. It stands for a version of the library other than the one ExternalUser was
/// compiled against, one whose Kettle has no method Pour and no property Hot beside ExternalUser's
/// extension methods of those names, and no interface IVessel, which ExternalUser's Fill asks of
/// its receiver. A fixture project cannot stand in for it, since it would have to build a second
/// ExternalLib.dll.
/// </summary>
internal static class BareKettleAssembly
{
    /// <summary>Write the assembly as ExternalLib.dll into <paramref name="folder"/>.</summary>
    public static void Write(string folder)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("ExternalLib") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("ExternalLib");

        var kettle = module.DefineType("ExternalLib.Extended.Kettle", TypeAttributes.Public | TypeAttributes.Class);
        kettle.DefineDefaultConstructor(MethodAttributes.Public);
        kettle.CreateType();
        assembly.Save(Path.Combine(folder, "ExternalLib.dll"));
    }
}
