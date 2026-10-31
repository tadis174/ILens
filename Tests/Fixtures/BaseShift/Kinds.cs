using System;

namespace BaseShift.Kinds
{
    // Names that stand for members of another kind in each assembly, for the tests on compare
    // asked for one of them by name. BaseShift2 compiles this file with SHIFTED defined: in Till,
    // the field Limit becomes a property, the method Open a property, the constant Size a
    // property, the property Ready a method, the constant Spare a field of another type, the field
    // Changed an event, and the constant Margin a field of its type that no constructor sets; Till's
    // field Delay gives way to a property of Store, which Till inherits, and the field Width, which
    // Till inherits from Store, to a property of Till's own; and Till's property IsEmpty gives way
    // to an extension method of TillExtensions.
    public class Store
    {
#if SHIFTED
        public int Delay { get; set; }
#else
        public int Width;
#endif
    }

    public class Till : Store
    {
#if SHIFTED
        public int Limit { get; set; }

        public bool Open => true;

        public int Size => 4;

        public bool Ready() => true;

        public static readonly long Spare = 3;

        public event EventHandler Changed;

        public static readonly int Margin;

        public int Width { get; set; }
#else
        public int Limit = 1;

        public void Open() { }

        public const int Size = 4;

        public bool Ready => true;

        public const int Spare = 3;

        public EventHandler Changed;

        public const int Margin = 0;

        public int Delay;

        public bool IsEmpty => true;
#endif

        public void Ring() => Changed?.Invoke(this, EventArgs.Empty);
    }

#if SHIFTED
    public static class TillExtensions
    {
        public static bool IsEmpty(this Till till) => true;
    }
#endif
}
