namespace ExternalUser.Inherited
{
    public interface IReading
    {
        int Level();
        int Peak { get; }
    }

    // Lists IReading and declares nothing. Level is bound to Sensor's method as it stands, since
    // that is virtual. Peak is not, so the compiler adds a method to Probe that implements
    // IReading's getter and passes the call on.
    public class Probe : ExternalLib.Inherited.Sensor, IReading
    {
    }
}
