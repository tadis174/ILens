namespace BaseShift.Keywords
{
    // Names C# reads as keywords, for the tests on compare's lines for them. BaseShift2 compiles
    // this file with SHIFTED defined: Relay's Send renames its parameter obj to object, which its
    // declaration line writes @object, and its field base and its method checked stay as they are.
    public class Relay
    {
        public int @base;

#if SHIFTED
        public void Send(object @object) { }
#else
        public void Send(object obj) { }
#endif

        public void @checked() { }
    }
}
