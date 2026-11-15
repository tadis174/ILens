using System.Reflection;

namespace ExternalLib.Versions
{
    /// <summary>
    /// An interface whose member takes a class of the framework that an old framework's
    /// System.Runtime does not hold, such as Mono's or the .NET Framework's. Built for .NET
    /// Standard, it names the class through netstandard, while a class built for .NET 10 that
    /// implements it names the class through System.Runtime.
    /// </summary>
    public interface IVersionLabel
    {
        string Label(AssemblyName name);
    }
}
