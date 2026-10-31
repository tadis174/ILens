namespace ExternalLib.Namesakes
{
    /// <summary>
    /// Types whose names ExternalUser gives types of its own in this namespace, as a library can
    /// define generic markers named like the engine's marker it uses: a class without type
    /// parameters beside two generic ones of that name, one beside a single generic one, a
    /// generic class beside one with another count of type parameters, and a generic class beside
    /// one without type parameters.
    /// </summary>
    public class Marker
    {
        public void Begin() { }

        public class Scope { }
    }

    public class Gauge
    {
        public void Read() { }
    }

    public class Crate<T>
    {
        public void Fill(T item) { }
    }

    public class Lever<T> { }

    /// <summary>
    /// A property with an init accessor, whose signature names this library's IsExternalInit, so
    /// that ExternalUser, which declares its own, references a type of a name it defines.
    /// </summary>
    public class Badge
    {
        public string Label { get; init; }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>The type an init accessor names, which netstandard2.0 lacks.</summary>
    internal static class IsExternalInit { }
}
