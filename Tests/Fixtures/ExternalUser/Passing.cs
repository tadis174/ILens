using ExternalLib.Passing;

namespace ExternalUser.Passing
{
    /// <summary>
    /// Calls the methods of ExternalLib.Passing, which take a parameter by reference with each
    /// keyword, on an interface and on a class. A call says that a parameter is passed by
    /// reference, and for an in or ref readonly parameter of a virtual method that the method
    /// only reads it; the keyword itself is in ExternalLib alone.
    /// </summary>
    public class Shipper
    {
        public Crate Held;

        public int Ship(IDepot depot, DepotBase root)
        {
            var crate = new Crate();
            int total = depot.Peek(in crate) + depot.Probe(in crate);
            depot.TryTake(1, out Crate taken);
            depot.Swap(ref crate);
            total += root.Plain(in crate);
            total += root.Weigh(crate) + root.Weigh(in crate) + root.Count(crate) + root.Count(in crate);
            return total + taken.Weight;
        }

        // A field of its own, passed by in to an interface method of the other assembly.
        public int Show(IDepot depot) => depot.Peek(in Held);
    }

    // Declares nothing: every member it has is one ExternalLib declares, which a member list
    // with the inherited members reads from that library.
    public class OwnDepot : Depot
    {
    }
}
