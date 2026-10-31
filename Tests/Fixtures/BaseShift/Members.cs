using System;

namespace BaseShift.Members
{
    // BaseShift2 compiles this file with SHIFTED defined. IGrowing then declares a method, a
    // property and an event more, and one method of Scales has another body. Nothing else in
    // this file differs between the two assemblies.
    public interface IGrowing
    {
        void Kept();
#if SHIFTED
        void Added();
        int AddedValue { get; }
        event EventHandler AddedChanged;
#endif
    }

    // Two virtual overloads that differ only in how the parameter is passed, which a comparison
    // has to tell apart to match each with its own. With SHIFTED the second one's body differs.
    public class Scales
    {
        public virtual int Weigh(int load) => load;

#if SHIFTED
        public virtual int Weigh(in int load) => load + 2;
#else
        public virtual int Weigh(in int load) => load + 1;
#endif
    }
}
