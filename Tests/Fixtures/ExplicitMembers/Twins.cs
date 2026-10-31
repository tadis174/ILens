namespace ExplicitMembers.Twins
{
    public interface ILeft
    {
        void Reset();
        int Level { get; set; }
    }

    public interface IRight
    {
        void Reset();
    }

    // One Reset implements the Reset of both interfaces, and Level the property of one.
    public class Both : ILeft, IRight
    {
        public void Reset() { }

        public int Level { get; set; }
    }
}
