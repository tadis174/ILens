using System;

namespace CallShapes
{
    /// <summary>The accessor-call search target: a property, an indexer and an event.</summary>
    public class Gauge
    {
        private readonly int[] _slots = new int[8];

        public int Level { get; set; }

        public int this[int slot]
        {
            get => _slots[slot];
            set => _slots[slot] = value;
        }

        public event EventHandler Changed;

        // Reads the property through its bare name, which the decompiler writes without a target.
        public int Doubled() => Level * 2;

        // Raising the event reads the field that holds its subscribers and calls no accessor.
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// An indexer that returns by reference and so has no setter: assigning to it calls the
    /// getter and writes through the reference it returns.
    /// </summary>
    public class Tape
    {
        private readonly int[] _cells = new int[8];

        public ref int this[int cell] => ref _cells[cell];
    }

    /// <summary>
    /// A property without a setter, assigned in the constructor. The assignment writes the
    /// backing field; only the read after it calls the getter.
    /// </summary>
    public class Meter
    {
        public Meter(int reading)
        {
            Reading = reading;
            Peak = Reading;
        }

        public int Reading { get; }

        public int Peak { get; set; }
    }

    /// <summary>
    /// One use of <see cref="Gauge"/>'s accessors per syntax that calls one: a read, an
    /// assignment, a compound assignment, an increment, an object initializer, an indexer read
    /// and write, a deconstructing assignment, subscribing to and unsubscribing from the event,
    /// and an assignment through an indexer that returns by reference.
    /// </summary>
    public class GaugeUsers
    {
        public int Read(Gauge gauge) => gauge.Level;

        public void Assign(Gauge gauge) => gauge.Level = 5;

        public void CompoundAssign(Gauge gauge) => gauge.Level += 3;

        public void Increment(Gauge gauge) => gauge.Level++;

        public Gauge Initialize() => new Gauge { Level = 7 };

        public int ReadSlot(Gauge gauge) => gauge[2];

        public void AssignSlot(Gauge gauge) => gauge[4] = 8;

        public void Deconstruct(Gauge gauge) => (gauge.Level, gauge[1]) = (10, 11);

        public void Subscribe(Gauge gauge, EventHandler handler) => gauge.Changed += handler;

        public void Unsubscribe(Gauge gauge, EventHandler handler) => gauge.Changed -= handler;

        public void AssignByReference(Tape tape) => tape[3] = 9;
    }
}
