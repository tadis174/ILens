using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly named ExternalLib whose class ExternalLib.Vault declares no method. It
/// stands for a version of the library other than the one ExternalUser was compiled against.
/// A fixture project cannot stand in for it, since it would have to build a second
/// ExternalLib.dll.
/// </summary>
internal static class EmptyVaultAssembly
{
    /// <summary>Write the assembly as ExternalLib.dll into <paramref name="folder"/>.</summary>
    public static void Write(string folder)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("ExternalLib") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("ExternalLib");

        var vault = module.DefineType("ExternalLib.Vault", TypeAttributes.Public | TypeAttributes.Class);
        vault.DefineDefaultConstructor(MethodAttributes.Public);
        vault.CreateType();
        assembly.Save(Path.Combine(folder, "ExternalLib.dll"));
    }
}
