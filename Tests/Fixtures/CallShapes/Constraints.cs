using System;

namespace CallShapes.Constraints
{
    // Extension methods whose receiver is a type parameter, one for each kind of constraint, for
    // the tests on finding such a method through a type that fits its constraints, on the mark
    // that names them, and on the types extension_methods lists it under. Plate and Spot
    // implement IShape, Pebble fits IShape through its base class Plate, Crate and Hue fit only
    // the methods without a type constraint, and Mold, being abstract, does not fit new(). Fit
    // stands beside a method of its name whose receiver is Plate itself. Plate's property Shade
    // stands beside two extension methods of its name that Plate fits, and Crate's property Label
    // beside two whose receiver is Crate itself, for the rule that a member of the name comes
    // before any extension method.
    public interface IShape
    {
    }

    public class Plate : IShape
    {
        public int Shade { get; set; }
    }

    public class Pebble : Plate
    {
    }

    public struct Spot : IShape
    {
        public int Size;
    }

    public class Crate
    {
        public string Label { get; set; }
    }

    public abstract class Mold
    {
    }

    public enum Hue
    {
        Red,
    }

    public static class ShapeExtensions
    {
        public static T Tag<T>(this T value) where T : class => value;

        public static void Grow<T>(this ref T shape) where T : struct, IShape { }

        public static int Corners<T>(this T shape) where T : IShape => 4;

        public static T Echo<T>(this T value) => value;

        public static T Zero<T>(this T value) where T : struct => default(T);

        public static string Spell<T>(this T hue) where T : struct, Enum => hue.ToString();

        public static T Fresh<T>(this T value) where T : new() => value;

        public static void Fit(this Plate plate) { }

        public static void Fit<T>(this T shape) where T : IShape { }

        public static T Shade<T>(this T shape, int level) where T : IShape => shape;

        public static T Shade<T>(this T shape, string name) where T : IShape => shape;

        public static Crate Label(this Crate crate, int number) => crate;

        public static Crate Label(this Crate crate, string text) => crate;
    }

    public static class Caller
    {
        public static void Use(Plate plate, Pebble pebble, Spot spot, Crate crate, Hue hue)
        {
            crate.Tag();
            spot.Grow();
            pebble.Corners();
            hue.Echo();
            spot.Zero();
            hue.Spell();
            crate.Fresh();
            plate.Fit();
            spot.Fit();
            plate.Shade(2);
            crate.Label("lid");
        }
    }
}
