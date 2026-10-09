using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BaseShift.Generated
{
    // BaseShift2 compiles this file with SHIFTED defined. Renumbered then gains Inserted, declared
    // before its other methods. The compiler numbers the code it generates for a lambda, a local
    // function, an iterator or an async method by the position of the method it is written in,
    // and numbers a few other generated names per type or per assembly in order of use. So every
    // generated name in Renumbered shifts, while no code in it changes. ChangedInside changes only
    // inside a lambda, a local function, an iterator and an async method.

    public class RenumberedBase
    {
        public virtual IEnumerable<int> Values()
        {
            yield return 0;
        }

        public virtual int Number() => 0;
    }

    public class Renumbered : RenumberedBase
    {
        private int _offset = 1;

#if SHIFTED
        // Uses what the methods below use, each in a form of its own, so that the counters per
        // type and per assembly shift along with the method numbers.
        public long Inserted(int seed)
        {
            Func<int> capturesThis = () => _offset;
            Func<int, int> capturesNothing = x => x - 1;
            Func<int> capturesLocal = () => seed;
            Func<int> callsBase = () => base.Number() + seed;
            Func<int, int> methodGroup = Halve;
            var anonymous = new { Inserted = seed };
            var withDefault = (long value = 1) => value;
            return capturesThis() + capturesNothing(seed) + capturesLocal() + callsBase()
                + methodGroup(seed) + anonymous.Inserted + withDefault();
        }
#endif

        public int CapturesThis()
        {
            Func<int> read = () => _offset;
            return read();
        }

        public int CapturesNothing()
        {
            Func<int, int> increment = x => x + 1;
            return increment(2);
        }

        public int CapturesLocal(int seed)
        {
            Func<int> twice = () => seed * 2;
            return twice();
        }

        public int LocalFunction(int seed)
        {
            return Twice(seed);

            int Twice(int x) => x * 2;
        }

        public int LocalFunctionCapturing(int seed)
        {
            return AddSeed(1);

            int AddSeed(int x) => x + seed;
        }

        public IEnumerable<int> Iterator()
        {
            yield return _offset;
        }

        public async Task<int> Async()
        {
            await Task.Yield();
            return _offset;
        }

        public override IEnumerable<int> Values()
        {
            foreach (var value in base.Values())
                yield return value;
        }

        public object Anonymous() => new { Value = _offset };

        public int MethodGroup()
        {
            Func<int, int> triple = Triple;
            return triple(2);
        }

        public int WithDefault()
        {
            var add = (int value = 1) => value + 1;
            return add();
        }

        public int Generic<T>(T item)
        {
            Func<T, int> count = _ => 1;
            return count(item);
        }

        public int GenericMethodGroup<T>(T item)
        {
            Func<T, int> measure = Measure;
            return measure(item);
        }

        private static int Measure<U>(U item) => 1;

        private static int Halve(int x) => x / 2;

        private static int Triple(int x) => x * 3;
    }

    public class ChangedInside
    {
        public int InLambda(int seed)
        {
#if SHIFTED
            Func<int> scaled = () => seed * 3;
#else
            Func<int> scaled = () => seed * 2;
#endif
            return scaled();
        }

        public int InLocalFunction(int seed)
        {
            return Scale(seed);

#if SHIFTED
            int Scale(int x) => x * 3;
#else
            int Scale(int x) => x * 2;
#endif
        }

        public IEnumerable<int> InIterator()
        {
#if SHIFTED
            yield return 3;
#else
            yield return 2;
#endif
        }

        public async Task<int> InAsync()
        {
            await Task.Yield();
#if SHIFTED
            return 3;
#else
            return 2;
#endif
        }

        public int Unchanged(int seed)
        {
            Func<int> scaled = () => seed * 2;
            return scaled();
        }
    }
}
