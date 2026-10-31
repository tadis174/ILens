using System.Collections.Generic;

namespace BaseShift.Overloads.Left
{
    public class Token { }

    public class Shelf
    {
        public class Entry { }
    }
}

namespace BaseShift.Overloads.Right
{
    public class Token { }

    public class Crate
    {
        public class Entry { }
    }
}

namespace BaseShift.Overloads
{
    // Each class but Replaced declares two overloads that a comparison has to tell apart by their
    // types: ILens's short form writes them alike, or, in ByRank, they differ only in an array's
    // number of dimensions. BaseShift2 compiles this file with SHIFTED, which gives the second
    // overload of each another body. A comparison has to match each overload with its own, so
    // that each of these classes has one changed member.

    public class ByNamespace
    {
        public int Take(Left.Token token) => 1;
#if SHIFTED
        public int Take(Right.Token token) => 3;
#else
        public int Take(Right.Token token) => 2;
#endif
    }

    public class ByNesting
    {
        public int Take(Left.Shelf.Entry entry) => 1;
#if SHIFTED
        public int Take(Right.Crate.Entry entry) => 3;
#else
        public int Take(Right.Crate.Entry entry) => 2;
#endif
    }

    public class ByRank
    {
        public int Take(int[] values) => 1;
#if SHIFTED
        public int Take(int[,] values) => 3;
#else
        public int Take(int[,] values) => 2;
#endif
    }

    // Conversion operators may differ in their return type alone.
    public class ByReturn
    {
        public static implicit operator Left.Token(ByReturn value) => null;
#if SHIFTED
        public static implicit operator Right.Token(ByReturn value) => new Right.Token();
#else
        public static implicit operator Right.Token(ByReturn value) => null;
#endif
    }

    public class ByIndex
    {
        public int this[Left.Token token] => 1;
#if SHIFTED
        public int this[Right.Token token] => 3;
#else
        public int this[Right.Token token] => 2;
#endif
    }

    public class ByArgument
    {
        public int Take(List<Left.Token> tokens) => 1;
#if SHIFTED
        public int Take(List<Right.Token> tokens) => 3;
#else
        public int Take(List<Right.Token> tokens) => 2;
#endif
    }

    // The second method's type parameter hides the class's of the same name.
#pragma warning disable CS0693
    public class ByParameter<T>
    {
        public int Take<U>(T value) => 1;
#if SHIFTED
        public int Take<T>(T value) => 3;
#else
        public int Take<T>(T value) => 2;
#endif
    }
#pragma warning restore CS0693

    // Between the two assemblies, Hold's parameter type gives way to a class of the same short
    // name in another namespace, and Pass's type parameter is renamed. Neither body changes.
    public class Replaced
    {
#if SHIFTED
        public int Hold(Right.Token token) => 1;

        public TItem Pass<TItem>(TItem item) => item;
#else
        public int Hold(Left.Token token) => 1;

        public T Pass<T>(T item) => item;
#endif
    }
}
