using System;
using System.ComponentModel;

namespace BaseShift.Foreign
{
    // Classes whose base class or interface another assembly defines, the framework here, which
    // ILens's own type system does not load. BaseShift2 compiles this file with SHIFTED defined:
    // Mover's base class EventArgs gives way to CancelEventArgs, and Switcher's interface
    // IComparable to ICloneable. Switcher has both methods in both assemblies, so its interface
    // is all that changes.
    public class Mover :
#if SHIFTED
        CancelEventArgs
#else
        EventArgs
#endif
    {
    }

    public class Switcher :
#if SHIFTED
        ICloneable
#else
        IComparable
#endif
    {
        public object Clone() => this;

        public int CompareTo(object obj) => 0;
    }
}
