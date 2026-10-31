using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BaseShift.Bodies
{
    // Methods without a body, which compare answers with why neither build has one. Native's two
    // are extern in both assemblies, one through DllImport and one as a call into the runtime's
    // own code. BaseShift2 compiles this file with SHIFTED defined, and Hatch's Open, abstract
    // here, is extern there. Nothing else in this file differs between the two assemblies.
    public class Native
    {
        [DllImport("kernel32.dll")]
        public static extern bool Beep(uint frequency, uint duration);

        [MethodImpl(MethodImplOptions.InternalCall)]
        public extern int Count();
    }

    public abstract class Hatch
    {
#if SHIFTED
        [MethodImpl(MethodImplOptions.InternalCall)]
        public extern void Open();
#else
        public abstract void Open();
#endif
    }
}
