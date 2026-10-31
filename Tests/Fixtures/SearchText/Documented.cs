using System;

namespace SearchText
{
    /// <summary>
    /// A class and a class nested in it, each with an attribute and a base type in its header,
    /// whose members the tests document in an XML file they write beside a copy of this assembly.
    /// The decompiler then writes a documentation comment above each documented member, inside
    /// the class, and the header lines still stand under their class.
    /// </summary>
    [Serializable]
    public class Documented : IDisposable
    {
        public void Dispose()
        {
        }

        [Serializable]
        public class Part : IComparable
        {
            public int CompareTo(object other)
            {
                return 0;
            }
        }
    }
}
