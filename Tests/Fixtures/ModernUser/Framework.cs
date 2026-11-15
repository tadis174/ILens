using System.Reflection;
using ExternalLib.Versions;

namespace ModernUser
{
    /// <summary>
    /// Implements a member of an interface built for .NET Standard that takes a class an old
    /// framework's System.Runtime does not hold: the interface names the class through
    /// netstandard, this class through System.Runtime.
    /// </summary>
    public class VersionLabel : IVersionLabel
    {
        public string Label(AssemblyName name) => name.Name;
    }

    /// <summary>
    /// A constant of flags whose enum an old framework's System.Runtime does not hold, so that
    /// code decompiled against that framework writes a number in place of the flags' names.
    /// </summary>
    public static class Lookup
    {
        public const BindingFlags Everything =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    }

    /// <summary>An enum, whose base class comes from the framework the reference search takes.</summary>
    public enum Shade
    {
        Light,
        Dark
    }
}
