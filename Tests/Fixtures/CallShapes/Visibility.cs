using System;

namespace CallShapes
{
    /// <summary>
    /// Members of every accessibility, a static constructor beside two instance constructors,
    /// and one lambda that captures, which the compiler moves into a nested class of its own.
    /// For the tests on what the tools that list say they left out, on a compiler-generated
    /// class named outright, and on finding a constructor by the name of its type.
    /// </summary>
    public class Vault
    {
        private int _stored;

        internal int Capacity;

        public int Count;

        static Vault()
        {
        }

        public Vault()
        {
        }

        public Vault(int stored)
        {
            _stored = stored;
            Capacity = stored;
        }

        public int Read() => _stored;

        protected void Rekey()
        {
        }

        internal void Reseal()
        {
        }

        private int Seal(int key) => key + _stored;

        public Func<int> Meter(int step) => () => step + 1;
    }
}

namespace CallShapes.Concealed
{
    /// <summary>
    /// The one type anyone wrote in its namespace, and internal. Its lambda captures nothing,
    /// so the compiler moves it into a nested class. A search of the namespace therefore shows
    /// nothing and leaves out one type for each of the two reasons.
    /// </summary>
    internal class Ledger
    {
        internal Func<int> Next() => () => 1;
    }
}
