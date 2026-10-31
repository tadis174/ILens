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
