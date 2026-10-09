using System;

namespace BaseShift
{
    // BaseShift2 compiles this file with SHIFTED defined. Gauge then differs only inside
    // accessor bodies: Level's getter, Limit's setter and Changed's add accessor. Its
    // signatures, its other accessors and Steady are the same in both assemblies.
    public class Gauge
    {
        private int _value;
        private int _subscriptions;
        private EventHandler _changed;

        public int Level
        {
#if SHIFTED
            get { return _value + 1; }
#else
            get { return _value; }
#endif
            set { _value = value; }
        }

        public int Limit
        {
            get { return _value; }
#if SHIFTED
            set { _value = value * 2; }
#else
            set { _value = value; }
#endif
        }

        public event EventHandler Changed
        {
#if SHIFTED
            add { _changed += value; _subscriptions++; }
#else
            add { _changed += value; }
#endif
            remove { _changed -= value; }
        }

        public int Steady { get; set; }
    }
}
