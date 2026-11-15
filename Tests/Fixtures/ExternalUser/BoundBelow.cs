using System;
using ExternalLib;

namespace ExternalUser.BoundBelow
{
    // Lists no interface. Mount, below it, lists ExternalLib's IGauge and ExternalUser's
    // ILocalGauge and binds their members by name to what it inherits from here, so only a look
    // below Housing finds what its members implement. Tick is private and Zero static, so no
    // class below binds them by name, and a constructor implements nothing.
    public class Housing
    {
        public Housing() { }

        public int Read() => 0;

        public string Unit => "bar";

        public event EventHandler Moved
        {
            add { }
            remove { }
        }

        public void Reset() { }

        private void Tick() { }

        public static void Zero() { }
    }

    public class Mount : Housing, IGauge, ILocalGauge
    {
    }

    // Lists no interface either, and Plug, below it, lists IGauge alone, so where ExternalLib
    // does not load, no interface of Plug's was read.
    public class Socket
    {
        public int Read() => 0;

        public string Unit => "bar";

        public event EventHandler Moved
        {
            add { }
            remove { }
        }
    }

    public class Plug : Socket, IGauge
    {
    }

    // Hood lists IGauge below Shell, which overrides Chassis's Read and hides its Unit, so
    // Chassis's Read and Unit implement nothing for Hood, whatever IGauge declares, while its
    // Moved can.
    public class Chassis
    {
        public virtual int Read() => 0;

        public string Unit => "bar";

        public event EventHandler Moved
        {
            add { }
            remove { }
        }
    }

    public class Shell : Chassis
    {
        public override int Read() => 1;

        public new string Unit => "psi";
    }

    public class Hood : Shell, IGauge
    {
    }
}
