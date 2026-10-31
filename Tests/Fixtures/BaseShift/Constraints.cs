namespace BaseShift.Constraints
{
    // An extension method whose receiver is a type parameter, which Box fits in this assembly
    // alone, since BaseShift2's Box does not implement IShape, and Crate fits in both. compare
    // asked for the method through Box says that the twin declares it all the same.
    public interface IShape
    {
    }

#if SHIFTED
    public class Box
    {
    }
#else
    public class Box : IShape
    {
    }
#endif

    public class Crate : IShape
    {
    }

    public static class ShapeExtensions
    {
        public static int Corners<T>(this T shape) where T : IShape => 4;
    }
}
