namespace CallShapes.Receivers
{
    // Extension methods in each shape a declaration line writes this in, beside a static method
    // that takes a Lamp without it, for the tests on how list_members writes an extension method
    // and on the header of one found from the type it extends: from Lamp itself, from DeskLamp,
    // which derives from Lamp, and for one that extends object.
    public class Lamp
    {
    }

    public class DeskLamp : Lamp
    {
    }

    public struct Charge
    {
        public int Level;
    }

    public static class LampExtensions
    {
        public static void Dim(this Lamp lamp) { }

        public static void Brighten(this Lamp lamp, params int[] steps) { }

        public static void Drain(this ref Charge charge) { charge.Level--; }

        public static int Measure(this in Charge charge) => charge.Level;

        public static string LabelOf(this object value) => "lamp";

        public static void Replace(Lamp lamp) { }
    }
}
