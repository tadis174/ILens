namespace ExternalLib.Passing
{
    // A parameter passed by reference with each keyword, on every kind of method whose
    // signature the compiler marks for an in or ref readonly parameter: an interface's, an
    // abstract and a virtual one, an override, a delegate's, and an interface's indexer. The
    // methods that are not virtual carry no such mark and are the control.

    public struct Crate
    {
        public int Weight;
    }

    public interface IDepot
    {
        int Peek(in Crate crate);
        int Probe(ref readonly Crate crate);
        bool TryTake(int slot, out Crate crate);
        void Swap(ref Crate crate);
        int this[in Crate crate] { get; }
    }

    public abstract class DepotBase
    {
        public abstract int Peek(in Crate crate);
        public virtual int Probe(ref readonly Crate crate) => crate.Weight;

        public int Plain(in Crate crate) => crate.Weight;
        public int PlainProbe(ref readonly Crate crate) => crate.Weight;

        // Two pairs of overloads that differ only in how the parameter is passed.
        public virtual int Weigh(Crate crate) => 0;
        public virtual int Weigh(in Crate crate) => 1;
        public int Count(Crate crate) => 0;
        public int Count(in Crate crate) => 1;
    }

    public class Depot : DepotBase, IDepot
    {
        public override int Peek(in Crate crate) => crate.Weight;
        public override int Probe(ref readonly Crate crate) => crate.Weight;

        public bool TryTake(int slot, out Crate crate)
        {
            crate = default;
            return false;
        }

        public void Swap(ref Crate crate) { }
        public int this[in Crate crate] => crate.Weight;
    }

    public delegate int Inspector(in Crate crate);

    // Passes a field of its own by reference, one field for each way: to an interface method
    // by in and by ref readonly, to a method that is not virtual by in, and to an interface
    // method by ref.
    public class Porter
    {
        public Crate ToPeek;
        public Crate ToProbe;
        public Crate ToPlain;
        public Crate ToSwap;

        public int Carry(IDepot depot, Depot own) =>
            depot.Peek(in ToPeek) + depot.Probe(in ToProbe) + own.Plain(in ToPlain);

        public void Trade(IDepot depot) => depot.Swap(ref ToSwap);
    }
}
