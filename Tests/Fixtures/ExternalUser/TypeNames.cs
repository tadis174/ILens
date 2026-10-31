using System;
using ExternalLib;

namespace ExternalUser.TypeNames
{
    public class Own { }

    /// <summary>
    /// Names types of ExternalLib in each way code names a type, and uses no member of them,
    /// so that the type's name is all the code holds of it.
    /// </summary>
    public class Namer
    {
        public object Cast(object value) => (Casing)value;

        public bool Is(object value) => value is Casing;

        public object Array() => new Casing[1];

        public Type Token() => typeof(Casing);

        public Notch Default() => default(Notch);

        public object Constructed(object value) => (Tray<Own>)value;
    }
}
