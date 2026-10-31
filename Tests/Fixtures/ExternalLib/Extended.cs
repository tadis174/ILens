namespace ExternalLib.Extended
{
    // ExternalUser's Extended.cs declares extension methods for Kettle, its base class Appliance
    // and its interface IVessel, some of them named like a member of Kettle: a method that takes
    // other parameters, a property, a public static method and a private method.

    public interface IVessel
    {
        int Volume { get; }
    }

    public class Appliance
    {
        public void Plug() { }
    }

    public class Kettle : Appliance, IVessel
    {
        public int Volume => 1;

        public bool Hot { get; set; }

        public void Pour(int cups) { }

        public static void Reset() { }

        private void Descale() { }
    }

    // Has the extension methods for Kettle through its base class.
    public class TravelKettle : Kettle { }
}
