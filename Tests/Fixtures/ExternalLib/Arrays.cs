namespace ExternalLib.Arrays
{
    // A class and one derived from it, for the tests on an extension method for an array of a class
    // of another library: ExternalUser's Saw takes an array of Plank, which an array of Beam
    // converts to only where ILens reads this library.
    public class Plank
    {
    }

    public class Beam : Plank
    {
    }
}
