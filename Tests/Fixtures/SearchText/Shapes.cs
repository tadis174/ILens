using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SearchText
{
    /// <summary>
    /// The same call in each place the compiler moves code out of the method it is written in:
    /// a lambda, a lambda that captures, a local function, an iterator and an async method. In
    /// the decompiled C# each stands inside the method that wrote it.
    /// </summary>
    public class Shapes
    {
        public Action InLambda()
        {
            return () => Marker.Hit("lambda");
        }

        public Action InCapturingLambda(string where)
        {
            return () => Marker.Hit(where + " captured");
        }

        public void InLocalFunction()
        {
            Local();

            void Local()
            {
                Marker.Hit("local function");
            }
        }

        public IEnumerable<int> InIterator()
        {
            Marker.Hit("iterator");
            yield return 1;
        }

        public async Task InAsyncMethod()
        {
            await Task.Yield();
            Marker.Hit("async method");
        }
    }
}
