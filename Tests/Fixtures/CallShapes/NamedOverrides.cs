using System;

namespace CallShapes.NamedOverrides
{
    /// <summary>
    /// A virtual method with a type parameter of its own, overridden one level down, three levels
    /// down and in a class beside them, and a virtual method without one as the control. In a
    /// virtual call of the first the compiler names the nearest override the receiver's class
    /// sees; in a call of the second it names the declaration in this class.
    /// </summary>
    public class Relay
    {
        public virtual T Send<T>(T signal) => signal;

        public virtual int Ping(int count) => count;
    }

    public class Booster : Relay
    {
        public override T Send<T>(T signal) => signal;

        public override int Ping(int count) => count;
    }

    /// <summary>Declares nothing, so a call of <c>Send</c> on it names <see cref="Booster"/>'s override.</summary>
    public class Tower : Booster
    {
    }

    public class Beacon : Tower
    {
        // Through base., a call that is not virtual: it names Booster's override and runs it.
        public override T Send<T>(T signal) => base.Send(signal);
    }

    /// <summary>Overrides beside <see cref="Booster"/>, deriving from none of the classes below it.</summary>
    public class Buoy : Relay
    {
        public override T Send<T>(T signal) => signal;
    }

    /// <summary>One use of <c>Send</c> per receiver type and per form.</summary>
    public class Operator
    {
        public int OnRelay(Relay relay) => relay.Send(1);

        public int OnBooster(Booster booster) => booster.Send(2);

        public int OnTower(Tower tower) => tower.Send(3);

        public int OnBeacon(Beacon beacon) => beacon.Send(4);

        public int OnBuoy(Buoy buoy) => buoy.Send(5);

        // As a method group, which loads the override's address through the object.
        public Func<int, int> Group(Booster booster) => booster.Send<int>;

        // In a lambda that captures the receiver.
        public Func<int> Later(Tower tower) => () => tower.Send(6);

        // On the declaring class and on an overriding class in one member.
        public int Both(Relay relay, Booster booster) => relay.Send(7) + booster.Send(8);

        // The control: the call names Relay's declaration.
        public int PingBooster(Booster booster) => booster.Ping(9);
    }
}
