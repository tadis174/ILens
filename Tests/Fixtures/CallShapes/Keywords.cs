using System;
using System.Threading.Tasks;

namespace CallShapes.Keywords
{
    // Names C# reads as keywords, for the tests on how a declaration line writes a name and how a
    // tool reads one written with '@': each kind of name a declaration declares is a reserved
    // keyword once, beside parameters named after words that are keywords only in a query or in
    // an async method.

    public delegate void Signal(object sender, int @class);

    public interface IOps
    {
        void @do();
    }

    public class Lever : IOps
    {
        public int @base;

        public Lever(bool @switch)
        {
        }

        public int @fixed { get; set; }

        public event EventHandler @lock;

        public void @checked()
        {
        }

        public void Set(bool @abstract, int from, int await)
        {
        }

        public void Tune(int @default = 3)
        {
        }

        public async Task Wait(int await)
        {
            await Task.Yield();
        }

        public void Hold<@in>() where @in : class
        {
        }

        void IOps.@do()
        {
        }
    }

    public class Crate<@out>
    {
    }

    public class Puller
    {
        public void Pull()
        {
            var lever = new Lever(true);
            lever.Set(true, 1, 2);
            lever.Tune(5);
            lever.@base = lever.@fixed;
            lever.@lock += (sender, args) => { };
            lever.@checked();
            lever.Hold<string>();
            ((IOps)lever).@do();
        }
    }
}
