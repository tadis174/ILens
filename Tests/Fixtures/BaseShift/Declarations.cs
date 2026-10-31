using System;
using System.Collections.Generic;

namespace BaseShift.Declarations
{
    // Members that keep their name, parameter types and return type and change what their
    // declaration line shows, for the tests on compare's lines for a changed declaration.
    // BaseShift2 compiles this file with SHIFTED defined: each member of Methods changes one thing
    // of its declaration and nothing in its body; Reworked's Total changes its accessibility and
    // its body; each field of Fields changes its accessibility or a modifier, and Max its value as
    // well; Kinds' Stride becomes a constant, so the static constructor that set it is gone; each
    // property and the event of Accessors change their accessors or accessibility, and Depth gains
    // a setter; Shelf's Height becomes virtual with a body, and Cabinet's Label sealed; Motor's
    // Start, Speed and Started, abstract in both, hide Engine's virtual members of their names in
    // BaseShift and override them in BaseShift2, which the list_members tests on an abstract
    // override read as well; Receivers' Promote becomes an extension method and Demote stops
    // being one, which only the ExtensionAttribute the compiler adds records. Of Methods' members,
    // Gather gains params on a collection, which C# 13 allows, and Spread loses it. Each method of Conditions but Keep
    // changes the conditions on its type parameter: Shape's names a class of the same name in
    // another namespace in each build, so its lines read alike unless written in full, and Mend's
    // body changes as well. Patch overrides Fit, whose condition changes, and C# writes no where
    // clause on the override; Holder's condition changes, which is no member's, and Keeper's type
    // parameter is renamed with its condition kept, which compare does not count.
    public class Methods
    {
#if SHIFTED
        protected Methods() { }

        private void Open() { }

        public static int Count() => 3;

        public virtual void Close() { }

        public void Fill(int amount) { }

        public TItem Pick<TItem>(TItem item) => item;

        public int Sum(params int[] values) => values.Length;

        public int Gather(params IEnumerable<int> values) => 1;

        public int Spread(IEnumerable<int> values) => 2;
#else
        public Methods() { }

        public void Open() { }

        public int Count() => 3;

        public void Close() { }

        public void Fill(int count) { }

        public T Pick<T>(T item) => item;

        public int Sum(int[] values) => values.Length;

        public int Gather(IEnumerable<int> values) => 1;

        public int Spread(params IEnumerable<int> values) => 2;
#endif
    }

    public class Reworked
    {
#if SHIFTED
        internal int Total(int a) => a + 2;
#else
        public int Total(int a) => a + 1;
#endif
    }

    public class Fields
    {
#if SHIFTED
        public readonly int Width = 1;
        public static readonly int Limit = 4;
        internal int Level = 2;
        public static volatile int Ticks;
        internal const int Max = 6;
#else
        public int Width = 1;
        public static int Limit = 4;
        public int Level = 2;
        public static int Ticks;
        public const int Max = 5;
#endif
    }

    public class Kinds
    {
#if SHIFTED
        public const int Stride = 4;
#else
        public static readonly int Stride = 4;
#endif
    }

    public class Accessors
    {
#if SHIFTED
        public int Size { get; internal set; }

        public string Name { get; init; }

        public int Depth { get; set; }

        internal event EventHandler Opened;
#else
        public int Size { get; private set; }

        public string Name { get; set; }

        public int Depth { get; }

        public event EventHandler Opened;
#endif

        public void Ring() => Opened?.Invoke(this, EventArgs.Empty);
    }

    public abstract class Shelf
    {
#if SHIFTED
        public virtual int Height() => 0;
#else
        public abstract int Height();
#endif

        public virtual string Label() => "shelf";
    }

    public class Cabinet : Shelf
    {
        public override int Height() => 2;

#if SHIFTED
        public sealed override string Label() => "cabinet";
#else
        public override string Label() => "cabinet";
#endif
    }

    public class Engine
    {
        public virtual void Start() { }

        public virtual int Speed => 0;

        public virtual event EventHandler Started { add { } remove { } }
    }

    public abstract class Motor : Engine
    {
#if SHIFTED
        public abstract override void Start();

        public abstract override int Speed { get; }

        public abstract override event EventHandler Started;
#else
        public new abstract void Start();

        public new abstract int Speed { get; }

        public new abstract event EventHandler Started;
#endif
    }

    public static class Receivers
    {
#if SHIFTED
        public static void Promote(this Methods methods) { }

        public static void Demote(Methods methods) { }
#else
        public static void Promote(Methods methods) { }

        public static void Demote(this Methods methods) { }
#endif
    }

    public class Conditions
    {
#if SHIFTED
        public void Gain<T>(T item) where T : class { }

        public void Lose<T>(T item) { }

        public void Swap<T>(T item) where T : struct { }

        public void Widen<T>(T item) where T : class, new() { }

        public void Retype<T>(T item) where T : IComparable<T> { }

        public void Shape<T>(T item) where T : Beta.Badge { }

        public int Mend<T>(T item) where T : class => 2;

        public virtual void Fit<T>(T item) where T : struct { }
#else
        public void Gain<T>(T item) { }

        public void Lose<T>(T item) where T : class { }

        public void Swap<T>(T item) where T : class { }

        public void Widen<T>(T item) where T : class { }

        public void Retype<T>(T item) where T : IComparable { }

        public void Shape<T>(T item) where T : Alpha.Badge { }

        public int Mend<T>(T item) => 1;

        public virtual void Fit<T>(T item) where T : class { }
#endif

        public void Keep<T>(T item) where T : class { }
    }

    public class Patch : Conditions
    {
        public override void Fit<T>(T item) { }
    }

#if SHIFTED
    public class Holder<T> where T : class
#else
    public class Holder<T>
#endif
    {
        public T Value;
    }

#if SHIFTED
    public class Keeper<TItem> where TItem : class
#else
    public class Keeper<T> where T : class
#endif
    {
    }
}

namespace BaseShift.Declarations.Alpha
{
    public class Badge
    {
    }
}

namespace BaseShift.Declarations.Beta
{
    public class Badge
    {
    }
}

namespace System.Runtime.CompilerServices
{
    // The compiler needs this class to write an init accessor, and netstandard2.0 has none. Both
    // assemblies declare it, so it is no difference between them.
    internal static class IsExternalInit
    {
    }

    // The compiler marks a params collection with this class, which netstandard2.0 does not have
    // either.
    internal sealed class ParamCollectionAttribute : Attribute
    {
    }
}
