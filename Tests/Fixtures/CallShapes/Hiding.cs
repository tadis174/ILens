using System;

namespace CallShapes.Hiding
{
#pragma warning disable CS0067 // The events are subscribed to, never raised.

    /// <summary>
    /// The members <see cref="Shadowing"/> declares anew, one for each way a member can hide
    /// another, and beside them those that hide nothing although they share a name.
    /// </summary>
    public class Shadowed
    {
        public Shadowed(int size) { }

        public void WithNew() { }

        public void WithoutNew() { }

        public virtual void OverVirtual() { }

        public virtual void AsNewVirtual() { }

        public static void StaticOverStatic() { }

        public void StaticOverInstance() { }

        public void OtherParameters(int amount) { }

        internal void Internal() { }

        private void Private() { }

        public void ThroughPrivate() => Private();

        public int Level { get; set; }

        public event EventHandler Changed;

        public int this[int index]
        {
            get => index;
            set { }
        }
    }

    /// <summary>Declares nothing, so <see cref="Shadowing"/> hides members two levels up.</summary>
    public class Between : Shadowed
    {
        public Between(int size) : base(size) { }
    }

    /// <summary>
    /// Declares each member of <see cref="Shadowed"/> anew. A call on a <see cref="Shadowed"/>
    /// names that class's member and runs it, also when the object is a <see cref="Shadowing"/>,
    /// so nothing that uses only <see cref="Shadowed"/> uses a member of this class.
    /// </summary>
    public class Shadowing : Between
    {
        // Takes the parameters of Shadowed's constructor, which is not inherited and so not hidden.
        public Shadowing(int size) : base(size) { }

        public new void WithNew() { }

#pragma warning disable CS0108 // Hides without new, which compiles to the same code.
        public void WithoutNew() { }
#pragma warning restore CS0108

        // Not virtual itself, over a virtual method.
        public new void OverVirtual() { }

        // Starts a slot of its own instead of overriding.
        public new virtual void AsNewVirtual() { }

        public new static void StaticOverStatic() { }

        // Static, over an instance method.
        public new static void StaticOverInstance() { }

        // Another parameter list: an overload beside Shadowed's, which stays visible.
        public void OtherParameters(string text) { }

        internal new void Internal() { }

        // Shadowed's Private is out of reach here, so there is nothing to hide.
        public void Private() { }

        public new int Level { get; set; }

        public new event EventHandler Changed;

        public new int this[int index]
        {
            get => index;
            set { }
        }
    }

#pragma warning restore CS0067

    public class Bin<T>
    {
        public void Put(T item) { }
    }

    /// <summary>Hides a method of a generic base class, which code names through a reference.</summary>
    public class IntBin : Bin<int>
    {
        public new void Put(int item) { }
    }

    public interface IStarter
    {
        void WithNew();
    }

    /// <summary>
    /// Its WithNew hides <see cref="Shadowed"/>'s and implements <see cref="IStarter"/>'s, for
    /// which the compiler makes it virtual and sealed.
    /// </summary>
    public class Starter : Shadowed, IStarter
    {
        public Starter() : base(0) { }

        public new void WithNew() { }
    }

    /// <summary>
    /// Each method uses the member of <see cref="Shadowed"/> it is named after, and no member
    /// of a class that hides one.
    /// </summary>
    public static class OfShadowed
    {
        public static object Create() => new Shadowed(1);

        public static void WithNew(Shadowed shadowed) => shadowed.WithNew();

        public static void WithoutNew(Shadowed shadowed) => shadowed.WithoutNew();

        public static void OverVirtual(Shadowed shadowed) => shadowed.OverVirtual();

        public static void AsNewVirtual(Shadowed shadowed) => shadowed.AsNewVirtual();

        public static void StaticOverStatic() => Shadowed.StaticOverStatic();

        public static void StaticOverInstance(Shadowed shadowed) => shadowed.StaticOverInstance();

        public static void OtherParameters(Shadowed shadowed) => shadowed.OtherParameters(1);

        public static void Internal(Shadowed shadowed) => shadowed.Internal();

        public static void Private(Shadowed shadowed) => shadowed.ThroughPrivate();

        public static int ReadLevel(Shadowed shadowed) => shadowed.Level;

        public static void SetLevel(Shadowed shadowed) => shadowed.Level = 1;

        public static void Subscribe(Shadowed shadowed) => shadowed.Changed += null;

        public static void Unsubscribe(Shadowed shadowed) => shadowed.Changed -= null;

        public static int ReadIndex(Shadowed shadowed) => shadowed[0];

        public static void SetIndex(Shadowed shadowed) => shadowed[0] = 1;

        public static void Put(Bin<int> bin) => bin.Put(1);

        // A Shadowing seen as a Shadowed: the call names Shadowed's member and runs it.
        public static void ThroughShadowing(Shadowing shadowing) => ((Shadowed)shadowing).WithNew();

        public static Action AsDelegate(Shadowed shadowed) => shadowed.WithNew;
    }

    /// <summary>
    /// Each method uses the member of <see cref="Shadowing"/> it is named after, or the hiding
    /// member of <see cref="IntBin"/> or <see cref="Starter"/>.
    /// </summary>
    public static class OfShadowing
    {
        public static object Create() => new Shadowing(1);

        public static void WithNew(Shadowing shadowing) => shadowing.WithNew();

        public static void WithoutNew(Shadowing shadowing) => shadowing.WithoutNew();

        public static void OverVirtual(Shadowing shadowing) => shadowing.OverVirtual();

        public static void AsNewVirtual(Shadowing shadowing) => shadowing.AsNewVirtual();

        public static void StaticOverStatic() => Shadowing.StaticOverStatic();

        public static void StaticOverInstance() => Shadowing.StaticOverInstance();

        public static void OtherParameters(Shadowing shadowing) => shadowing.OtherParameters("");

        public static void Internal(Shadowing shadowing) => shadowing.Internal();

        public static void Private(Shadowing shadowing) => shadowing.Private();

        public static int ReadLevel(Shadowing shadowing) => shadowing.Level;

        public static void SetLevel(Shadowing shadowing) => shadowing.Level = 1;

        public static void Subscribe(Shadowing shadowing) => shadowing.Changed += null;

        public static void Unsubscribe(Shadowing shadowing) => shadowing.Changed -= null;

        public static int ReadIndex(Shadowing shadowing) => shadowing[0];

        public static void SetIndex(Shadowing shadowing) => shadowing[0] = 1;

        public static void Put(IntBin bin) => bin.Put(1);

        public static void OnStarter(Starter starter) => starter.WithNew();

        // Names IStarter's member, whichever class implements it.
        public static void ThroughInterface(IStarter starter) => starter.WithNew();

        public static Action AsDelegate(Shadowing shadowing) => shadowing.WithNew;
    }
}
