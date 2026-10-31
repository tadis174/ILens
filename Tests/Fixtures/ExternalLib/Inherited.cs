namespace ExternalLib.Inherited
{
    /// <summary>
    /// A virtual method and a property that is not virtual, both of which a class of another
    /// assembly inherits and binds an interface of its own to.
    /// </summary>
    public class Sensor
    {
        public virtual int Level() => 0;
        public int Peak => 0;
    }
}
