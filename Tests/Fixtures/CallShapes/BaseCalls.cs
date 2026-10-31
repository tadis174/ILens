using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CallShapes.BaseCalls
{
    /// <summary>The members the classes below call through <c>base.</c>.</summary>
    public class Gate
    {
        public virtual void Open(int width) { }

        public virtual void Close(int speed) { }

        public virtual void Lock(int code) { }

        public virtual void Latch() { }

        public virtual IEnumerable<int> Steps() => new int[0];

        public virtual int Count() => 0;

        public virtual int Weight() => 0;

        public virtual int Level => 0;

        public virtual T Pass<T>(T value) => value;
    }

    /// <summary>
    /// Calls <see cref="Gate"/>'s members through <c>base.</c> from code the compiler moves into
    /// another class. Such a call cannot be made from there, so the compiler adds a method to
    /// this class that makes it, and has the moved code call that method.
    /// </summary>
    public class Lodge : Gate
    {
        // In a lambda that captures a parameter, and nowhere else in the member.
        public override void Open(int width) => Runner.Run(() => base.Open(width));

        // In two lambdas of one member, through the method the compiler added for Open above.
        public void OpenTwice(int width)
        {
            Runner.Run(() => base.Open(width));
            Runner.Run(() => base.Open(width + 1));
        }

        // In a lambda and outside it.
        public override void Lock(int code)
        {
            Runner.Run(() => base.Lock(code));
            base.Lock(code + 1);
        }

        // In an iterator.
        public override IEnumerable<int> Steps()
        {
            foreach (var step in base.Steps())
                yield return step;
        }

        // In an async method.
        public async Task<int> CountLater()
        {
            await Task.Yield();
            return base.Count();
        }

        // A property read in an iterator.
        public IEnumerable<int> Levels()
        {
            yield return base.Level;
        }

        // In a lambda that captures nothing but this object. The compiler leaves it in this
        // class, where it calls the base method itself.
        public void LatchLater() => Runner.Run(() => base.Latch());

        // In a local function that only passes the call on, which compiles to a method of the
        // shape the compiler adds itself.
        public int WeightThroughLocal()
        {
            int Pass() => base.Weight();
            return Pass() + Pass();
        }

        // The same with a body in braces, which a debug build compiles to a return through a local.
        public int WeightThroughBlock()
        {
            int Pass()
            {
                return base.Weight();
            }
            return Pass();
        }

        // Such a local function called and also turned into a delegate, which keeps its own code
        // in the method.
        public int WeightThroughGroup()
        {
            int Pass() => base.Weight();
            Func<int> later = Pass;
            return Pass() + later();
        }
    }

    /// <summary>Overrides what <see cref="Arch"/> calls through <c>base.</c>.</summary>
    public class Portal : Gate
    {
        public override void Close(int speed) { }

        public override int Level => 1;
    }

    /// <summary>
    /// Calls members through <c>base.</c> that it does not override itself. The compiler calls
    /// <see cref="Portal"/>'s overrides without virtual dispatch, and in this class the plain
    /// name means the same member.
    /// </summary>
    public class Arch : Portal
    {
        public void Shut() => base.Close(3);

        public int Peek() => base.Level;
    }

    /// <summary>Overrides a method with a type parameter of its own and one without.</summary>
    public sealed class Vault : Gate
    {
        public override void Close(int speed) { }

        public override T Pass<T>(T value) => value;
    }

    /// <summary>
    /// Calls both on a <see cref="Vault"/>. The compiler names <see cref="Vault"/>'s override in
    /// the call of the generic <c>Pass</c>, and <see cref="Gate"/>'s declaration in the call of
    /// <c>Close</c>, as for any virtual method without a type parameter.
    /// </summary>
    public class Porter
    {
        public void Shut(Vault vault) => vault.Close(4);

        public int Hand(Vault vault) => vault.Pass(5);
    }

    public static class Runner
    {
        public static void Run(Action action) => action();
    }
}
