using System.Threading;

namespace BaseShift.Fields
{
    // Fields that are no constants, for the tests on the error compare gives for a field asked
    // for by name, which names the constructors that set it. BaseShift2 compiles this file with
    // SHIFTED defined: Late's Start gains an initializer, Shrinking loses Gone, Growing gains Fresh,
    // and Arrival is there alone. Nothing else differs, since the error does not depend on a
    // field's value.

    // The static constructor holds the initializer, and the class has no instance constructor.
    public static class Settings
    {
        public static readonly int Count = 5;
    }

    // The instance constructor holds nothing of the static field's initializer.
    public class Registry
    {
        public static readonly string Name = "main";
    }

    // The constructor that calls another of its class holds no initializer.
    public class Gauge
    {
        public int Size = 5;

        public Gauge() { }

        public Gauge(int scale) : this() { }

        public Gauge(string label) { }
    }

    // Asked for on Dial, the field is Panel's, and so are the constructors that set it.
    public class Panel
    {
        public static int Shared = 1;
    }

    public class Dial : Panel { }

    // No constructor sets Stock; a method does.
    public class Store
    {
        public int Stock;

        public void Fill() => Stock = 10;
    }

    public class Late
    {
#if SHIFTED
        public static int Start = 1;
#else
        public static int Start;
#endif
    }

    public class Shrinking
    {
#if !SHIFTED
        public int Gone = 1;
#endif

        public void Keep() { }
    }

    public class Growing
    {
#if SHIFTED
        public int Fresh = 1;
#endif

        public void Keep() { }
    }

    // The constructor passes the field by reference to a method of another assembly, whose code
    // says whether it writes the field.
    public class Tally
    {
        public static int Created;

        public Tally() => Interlocked.Increment(ref Created);
    }

    // The static constructor sets Count, and the constructor passes it by reference as Tally's does.
    public class Census
    {
        public static int Count = 1;

        public Census() => Interlocked.Increment(ref Count);
    }

#if SHIFTED
    public class Arrival
    {
        public static int Gate = 3;
    }
#endif

    // Code names a field of a generic class through a reference row, also inside the class.
    public class Pool<T>
    {
        public static int Capacity = 3;
    }

    // A struct's field initializer goes into each constructor it declares.
    public struct Extent
    {
        public int Width = 1;
        public int Height;

        public Extent(int height) => Height = height;
    }
}
