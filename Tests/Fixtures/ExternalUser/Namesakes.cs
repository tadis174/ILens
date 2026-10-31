using ExternalLib.Namesakes;

namespace ExternalLib.Namesakes
{
    // Types named like ExternalLib's types of this namespace, which this assembly uses as well:
    // two generic types named like its Marker, whose nested class each has too, one named like
    // its Gauge, one with another count of type parameters than its Crate<T>, and one without
    // type parameters named like its Lever<T>.

    public class Marker<T>
    {
        public class Scope { }
    }

    public class Marker<T, U>
    {
        public class Scope { }
    }

    public class Gauge<T> { }

    public class Crate<T, U> { }

    public class Lever { }
}

namespace ExternalUser.Namesakes
{
    /// <summary>Uses ExternalLib's types of the names this assembly also gives types of its own.</summary>
    public static class Readings
    {
        public static void Mark(Marker marker) => marker.Begin();

        public static void Enter(Marker.Scope scope) { }

        public static void Read(Gauge gauge) => gauge.Read();

        public static void Fill(Crate<int> crate) => crate.Fill(1);

        public static void Pull(Lever<int> lever) { }

        public static Badge Label() => new Badge { Label = "x" };
    }

    /// <summary>
    /// A property with an init accessor, for which this assembly declares its own IsExternalInit
    /// beside the one ExternalLib's Badge names.
    /// </summary>
    public class Note
    {
        public string Text { get; init; }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>The type an init accessor names, which netstandard2.0 lacks.</summary>
    internal static class IsExternalInit { }
}
