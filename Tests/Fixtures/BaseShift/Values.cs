namespace BaseShift.Values
{
    // BaseShift2 compiles this file with SHIFTED defined. Constants and enum members then keep
    // their names and types and take other values: Limits' Changed, Text, Empty and the bool Open,
    // which nothing in the assembly uses; Prices' decimal constant, which the compiler sets in the
    // static constructor; Shade's Green in place; Stage's Second and Third, which the member
    // Inserted moves up; Base's Limit, which Derived inherits; Token's Equals, named like the method
    // every enum inherits from System.Enum; and Tall's Height, which hides Shelf's method of that
    // name.
    // Limits' Kept and Ordinary and the enum Steady keep theirs, Limits gains Added and changes
    // Wide's type from int to long, and Width changes its underlying type from byte to short with
    // its values kept. Season is an enum only BaseShift2 has.

    public static class Limits
    {
#if SHIFTED
        public const int Changed = 6;

        public const string Text = "after";

        public const string Empty = "set";

        public const int Added = 7;

        public const long Wide = 5;

        public const bool Open = true;
#else
        public const int Changed = 5;

        public const string Text = "before";

        public const string Empty = null;

        public const int Wide = 5;

        public const bool Open = false;
#endif

        public const int Kept = 1;

        public static readonly int Ordinary = 3;
    }

    public static class Prices
    {
#if SHIFTED
        public const decimal Price = 2.5m;
#else
        public const decimal Price = 1.5m;
#endif
    }

    public enum Shade
    {
        Red = 1,
#if SHIFTED
        Green = 3,
#else
        Green = 2,
#endif
        Blue = 4,
    }

    public enum Stage
    {
        First,
#if SHIFTED
        Inserted,
#endif
        Second,
        Third,
    }

#if SHIFTED
    public enum Width : short
#else
    public enum Width : byte
#endif
    {
        Narrow,
        Broad,
    }

    public enum Steady
    {
        One,
        Two,
    }

#if SHIFTED
    public enum Season
    {
        Spring,
        Autumn,
    }
#endif

    public class Base
    {
#if SHIFTED
        public const int Limit = 6;
#else
        public const int Limit = 5;
#endif
    }

    public class Derived : Base
    {
    }

    public enum Token
    {
        Plus,
#if SHIFTED
        Minus,
#endif
        Equals,
    }

    public class Shelf
    {
        public int Height() => 1;
    }

    public class Tall : Shelf
    {
#if SHIFTED
        public new const int Height = 3;
#else
        public new const int Height = 2;
#endif
    }
}
