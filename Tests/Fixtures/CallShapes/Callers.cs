using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CallShapes
{
    /// <summary>The members every caller in this fixture uses.</summary>
    public static class Target
    {
        public static int Counter;

        public static void Record(string label, int delay = 0) { }
    }

    /// <summary>Instantiated by every caller, which then calls its virtual method.</summary>
    public class Widget
    {
        public virtual void Ping() { }
    }

    /// <summary>
    /// One caller per place the compiler moves code out of the method it is written in,
    /// plus a direct call as the control and two indexers, which share the name Item and
    /// are told apart only by their indices. Each body makes the same four uses: a call, a
    /// field read, a field write, and an instantiation followed by a virtual call. Only
    /// <see cref="Direct"/> and the indexers' getters hold them in their own IL bodies;
    /// every other body compiles into a method the compiler generates.
    /// </summary>
    public class Callers
    {
        public void Direct()
        {
            Target.Record("direct", Target.Counter);
            Target.Counter = 1;
            new Widget().Ping();
        }

        public int this[int index]
        {
            get
            {
                Target.Record("indexer", Target.Counter);
                Target.Counter = 1;
                new Widget().Ping();
                return index;
            }
        }

        public int this[int row, int column]
        {
            get
            {
                Target.Record("two-index indexer", Target.Counter);
                Target.Counter = 1;
                new Widget().Ping();
                return row + column;
            }
        }

        // No captured state: the lambda becomes a method on the shared "<>c" class.
        public Action InLambda() => () =>
        {
            Target.Record("lambda", Target.Counter);
            Target.Counter = 1;
            new Widget().Ping();
        };

        // Captures a parameter: the lambda becomes a method on a display class.
        public Action InCapturingLambda(int delay) => () =>
        {
            Target.Record("closure", delay + Target.Counter);
            Target.Counter = delay;
            new Widget().Ping();
        };

        // The lambda sits in a property getter, so its uses belong to the property.
        public Action InPropertyGetter => () =>
        {
            Target.Record("property", Target.Counter);
            Target.Counter = 1;
            new Widget().Ping();
        };

        public void InLocalFunction()
        {
            Local();

            void Local()
            {
                Target.Record("local function", Target.Counter);
                Target.Counter = 1;
                new Widget().Ping();
            }
        }

        public IEnumerable<int> InIterator()
        {
            Target.Record("iterator", Target.Counter);
            Target.Counter = 1;
            new Widget().Ping();
            yield return 1;
        }

        public async Task InAsyncMethod()
        {
            await Task.Yield();
            Target.Record("async", Target.Counter);
            Target.Counter = 1;
            new Widget().Ping();
        }
    }

    /// <summary>
    /// The same uses in a nested class. The decompiler builds its map from generated code
    /// back to source for the type that declares the handle it is given, so a lookup made
    /// with the nested class's own handle returns the outer class's map. The local function
    /// sits directly on the nested class, so it is attributed correctly only if the lookup
    /// avoids that handle. The lambda sits on a closure class nested inside, whose handle
    /// leads to the right map either way.
    /// </summary>
    public class Outer
    {
        public class Nested
        {
            public Action InLambda() => () =>
            {
                Target.Record("nested lambda", Target.Counter);
                Target.Counter = 1;
                new Widget().Ping();
            };

            public void InLocalFunction()
            {
                Local();

                void Local()
                {
                    Target.Record("nested local function", Target.Counter);
                    Target.Counter = 1;
                    new Widget().Ping();
                }
            }
        }
    }
}
