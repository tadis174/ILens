using System;

namespace BaseShift.Retyped
{
    // Names that stand for other members of one kind in each assembly, for the tests on compare
    // asked for one of them by name. BaseShift2 compiles this file with SHIFTED defined: in Till,
    // the field Limit, the static field Count, the property Depth, which also gains a setter, the
    // indexer and the event Opened take another type, and the field Tag a class of the same name
    // in another namespace; Till's field Delay moves to Store, and Till overrides Store's property
    // Label.
    public class Store
    {
#if SHIFTED
        public int Delay = 2;
#endif

        public virtual string Label => "store";
    }

    public class Till : Store
    {
#if SHIFTED
        public long Limit = 1;

        public static long Count = 2;

        public long Depth { get; set; }

        public long this[long index] => index;

        public event Action Opened;

        public Two.Tag Tag;

        public override string Label => "till";

        public void Open() => Opened?.Invoke();
#else
        public int Limit = 1;

        public static int Count;

        public int Depth { get; }

        public int this[int index] => index;

        public event EventHandler Opened;

        public One.Tag Tag;

        public int Delay = 2;

        public void Open() => Opened?.Invoke(this, EventArgs.Empty);
#endif
    }
}

namespace BaseShift.Retyped.One
{
    public class Tag { }
}

namespace BaseShift.Retyped.Two
{
    public class Tag { }
}
