using System;

namespace BaseShift.Signatures
{
    // BaseShift2 compiles this file with SHIFTED defined. A method's name then finds another
    // member in each assembly, as a comparison of the type matches members: Returns' methods
    // return another type, from the same body and from another; Descendant's Moved moves to its
    // base class; Extended's Run gives way to an extension method; and Disposer's Dispose to an
    // explicit implementation. Descendant inherits Kept and Reworked in both, Reworked with another
    // body in BaseShift2, and only BaseShift2 has the extension method Added. Of the extension
    // methods for the framework's String, Weigh has another body in BaseShift2, and only
    // BaseShift2 has Echo.

    public class Returns
    {
#if SHIFTED
        public string Open() => null;

        public long Count() => 1;
#else
        public object Open() => null;

        public int Count() => 1;
#endif
    }

    public class Ancestor
    {
        public void Kept() { }

#if SHIFTED
        public int Reworked() => 2;

        public void Moved() { }
#else
        public int Reworked() => 1;
#endif
    }

    public class Descendant : Ancestor
    {
#if !SHIFTED
        public void Moved() { }
#endif
    }

    public class Extended
    {
#if !SHIFTED
        public void Run() { }
#endif
    }

    public static class Extensions
    {
#if SHIFTED
        public static void Run(this Extended extended) { }

        public static void Added(this Extended extended) { }

        public static int Weigh(this string text) => text.Length * 2;

        public static string Echo(this string text) => text + text;
#else
        public static int Weigh(this string text) => text.Length;
#endif
    }

    public class Disposer : IDisposable
    {
#if SHIFTED
        void IDisposable.Dispose() { }
#else
        public void Dispose() { }
#endif
    }
}
