using ExternalLib.Extended;

namespace ExternalUser.Extended
{
    // Extension methods for ExternalLib's Kettle, for the tests on finding one through a class of
    // another assembly. Boil names no member of Kettle. Pour, Hot, Reset and Descale share their
    // names with a method of Kettle that takes other parameters, a property, a public static
    // method and a private method. Unplug extends Kettle's base class and Measure its interface.
    public static class KettleExtensions
    {
        public static void Boil(this Kettle kettle) => kettle.Plug();

        public static void Pour(this Kettle kettle, string drink) { }

        public static bool Hot(this Kettle kettle) => kettle.Volume > 0;

        public static void Reset(this Kettle kettle) { }

        public static void Descale(this Kettle kettle) { }

        public static void Unplug(this Appliance appliance) { }

        public static int Measure(this IVessel vessel) => vessel.Volume;
    }

    // Brew calls each extension method. C# binds it where Kettle's member of the name takes
    // other arguments, cannot be called, is static or cannot be reached from this assembly.
    // Serve and Restart call Kettle's own Pour and Reset.
    public class Cook
    {
        public void Brew(Kettle kettle)
        {
            kettle.Boil();
            kettle.Pour("tea");
            kettle.Hot();
            kettle.Reset();
            kettle.Descale();
            kettle.Unplug();
            kettle.Measure();
        }

        public void Serve(Kettle kettle) => kettle.Pour(2);

        public void Restart() => Kettle.Reset();
    }

    // Extension methods whose receiver is a type parameter, for the tests on finding one through a
    // class of another assembly: Fill fits a type that has IVessel, as Kettle does, and Label fits
    // every type. Stock calls both.
    public static class VesselExtensions
    {
        public static int Fill<T>(this T vessel) where T : IVessel => vessel.Volume;

        public static string Label<T>(this T item) => "label";
    }

    public class Pantry
    {
        public void Stock(Kettle kettle)
        {
            kettle.Fill();
            kettle.Label();
        }
    }
}
