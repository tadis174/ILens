using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CallShapes.SelfUse
{
    /// <summary>
    /// Calls its own methods from each place the compiler moves code out of the method it is
    /// written in, and from a plain method as the control. No other class uses it, so nothing
    /// but the class itself does. The lambda that captures a parameter, the iterator and the
    /// async method each get a class nested in this one, which holds this instance in a field.
    /// </summary>
    public class Hermit
    {
        private int _steps;

        private static int Origin() => 0;

        private int Step() => ++_steps;

        public int FromPlainMethod() => Step();

        // Captures nothing: the lambda becomes a method on the nested class "<>c".
        public Func<int> FromLambda() => () => Origin();

        public Func<int> FromCapturingLambda(int offset) => () => Step() + offset;

        public int FromLocalFunction()
        {
            return Local();

            int Local() => Step();
        }

        public IEnumerable<int> FromIterator()
        {
            yield return Step();
        }

        public async Task<int> FromAsyncMethod()
        {
            await Task.Yield();
            return Step();
        }
    }

    /// <summary>
    /// Calls its own method from a lambda that captures a parameter, like <see cref="Hermit"/>,
    /// and is used by <see cref="Lodger"/>.
    /// </summary>
    public class Innkeeper
    {
        private int _steps;

        private int Step() => ++_steps;

        public Func<int> FromCapturingLambda(int offset) => () => Step() + offset;
    }

    /// <summary>
    /// Uses <see cref="Innkeeper"/> from a lambda that captures it, so the compiler nests a
    /// class in this one with a field of that type.
    /// </summary>
    public class Lodger
    {
        public Func<int> Visit(Innkeeper innkeeper) => () => innkeeper.FromCapturingLambda(1)();
    }
}
