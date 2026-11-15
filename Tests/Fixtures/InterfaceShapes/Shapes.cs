using System;

namespace InterfaceShapes
{
    // A member of every shape C# allows in an interface, each as a method, a property and an
    // event. A declaration line shows no body, so each shape has to be told from the others by
    // its modifiers alone.
    public interface IEvery<TSelf> where TSelf : IEvery<TSelf>
    {
        // Without a body: the implementing type supplies it.
        void Plain();
        int PlainValue { get; set; }
        event EventHandler PlainChanged;
        int this[int index] { get; }

        // With a default implementation.
        void Defaulted() { }
        int DefaultedValue => 1;
        event EventHandler DefaultedChanged { add { } remove { } }

        // Sealed: a body no implementing type can replace.
        sealed void Fixed() { }
        sealed int FixedValue => 1;
        sealed event EventHandler FixedChanged { add { } remove { } }

        // Static with a body, as in a class.
        static void Shared() { }
        static int SharedValue => 1;
        static event EventHandler SharedChanged { add { } remove { } }

        // Static abstract: the implementing type supplies them.
        static abstract void SharedPlain();
        static abstract int SharedPlainValue { get; }
        static abstract event EventHandler SharedPlainChanged;
        static abstract TSelf operator +(TSelf left, TSelf right);

        // Static virtual: a default the implementing type may replace.
        static virtual void SharedDefaulted() { }
        static virtual int SharedDefaultedValue => 1;
        static virtual event EventHandler SharedDefaultedChanged { add { } remove { } }

        // The other accessibilities. A private member always has a body.
        private void Hidden() { }
        private int HiddenValue => 1;
        protected void Guarded();
        protected int GuardedValue { get; }
        internal void Inner();
    }

    public interface IFeed
    {
        void Open();
        void Close() { }
        int Count { get; }
        event EventHandler Moved;
    }

    // An interface that implements members of its base interface and makes one abstract again.
    public interface IBufferedFeed : IFeed
    {
        void IFeed.Open() { }
        abstract void IFeed.Close();
        int IFeed.Count => 2;
        event EventHandler IFeed.Moved { add { } remove { } }
    }

    // Members no class can implement, which an interface may declare since C# 8: static ones
    // that are neither abstract nor virtual, sealed ones and a private one. Stencil has the
    // interface and declares a public member of each of those names, which implements none of
    // them; the runtime's interface map for Stencil names Area, Trace, Guard, Scale and Changed's
    // accessors alone, which are abstract or virtual, as an interface's member with a body is by
    // default, Guard although it is protected, and Scale by its modifier. Tracer has the
    // interface below Outline, which declares a public Sides without it.
    public interface IStencil
    {
        static int Count => 0;
        static void Reset() { }
        sealed int Sides() => 0;
        sealed int Corners => 0;
        sealed event EventHandler Moved { add { } remove { } }
        private int Hidden() => 0;

        int Area();
        int Trace() => 0;
        protected int Guard() => 0;
        static virtual int Scale() => 0;
        event EventHandler Changed { add { } remove { } }
    }

    public class Stencil : IStencil
    {
        public static int Count => 1;
        public static void Reset() { }
        public int Sides() => 1;
        public int Corners => 1;
        public event EventHandler Moved { add { } remove { } }
        public int Hidden() => 1;

        public int Area() => 1;
        public int Trace() => 1;
        public int Guard() => 1;
        public static int Scale() => 1;
        public event EventHandler Changed { add { } remove { } }
    }

    public class Outline
    {
        public int Sides() => 2;
    }

    public class Tracer : Outline, IStencil
    {
        public int Area() => 2;
    }

    // The control: the same shapes in a class, whose members keep the modifiers a class writes.
    public abstract class Control
    {
        public abstract void Plain();
        public abstract int PlainValue { get; set; }
        public abstract event EventHandler PlainChanged;

        public virtual void Defaulted() { }
        public virtual int DefaultedValue => 1;
        public virtual event EventHandler DefaultedChanged { add { } remove { } }

        public void Fixed() { }
        public static void Shared() { }
    }

    public class Follower : Control
    {
        public override void Plain() { }
        public override int PlainValue { get; set; }
        public override event EventHandler PlainChanged { add { } remove { } }
        public sealed override void Defaulted() { }
    }
}
