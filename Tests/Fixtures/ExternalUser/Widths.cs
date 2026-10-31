using System;
using ExternalLib;
using ExternalUser.Widths;

// An attribute's bytes hold an enum's value without saying how many bytes it takes; only the
// assembly that defines the enum records that. These attributes are given values of
// ExternalLib's Grain, which takes one byte, and Reach, which takes eight, in each place where
// a value read with another width shifts what the attribute names after it.
[assembly: Gauge(typeof(OnAssembly), Grain.Fine)]

namespace ExternalUser.Widths
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class GaugeAttribute : Attribute
    {
        public GaugeAttribute() { }
        public GaugeAttribute(Type type, Grain grain) { }
        public GaugeAttribute(Grain grain, Type type) { }
        public GaugeAttribute(Type type, Reach reach) { }

        public Grain Named { get; set; }
        public Type Also { get; set; }
    }

    // The class the typeofs below name.
    public class Measured { }

    [Gauge(typeof(Measured), Grain.Fine)]
    public class ValueAfterTypeof { }

    [Gauge(Grain.Fine, typeof(Measured))]
    public class ValueBeforeTypeof { }

    [Gauge(Named = Grain.Fine, Also = typeof(Measured))]
    public class ValueNamed { }

    [Gauge(typeof(Measured), Reach.Far)]
    public class WideValue { }

    // Named only by the attribute on the assembly.
    public class OnAssembly { }

    // A class of ExternalLib beside a value.
    [Gauge(typeof(OnlyBesideGrain), Grain.Fine)]
    public class NamesLibraryClass { }

    // The attribute's bytes spell this class by its full name, which is 70 characters. Read
    // with two bytes for the value where it takes one, the value swallows the length; the
    // name's first character, 'E', is then read as the length 69, and 69 characters are left,
    // so that reading fits the bytes as well, and names no class there is. Another name or
    // namespace ends that.
    public class NamedAtTheLengthWhereReadingTheValueAsTwoBytesFits { }

    [Gauge(Grain.Fine, typeof(NamedAtTheLengthWhereReadingTheValueAsTwoBytesFits))]
    public class ReadTwoWays { }

    // The same with a class of ExternalLib, whose comment has the arithmetic.
    [Gauge(Grain.Fine, typeof(NamedWhereEightBytesFits))]
    public class LibraryClassReadTwoWays { }

    // An attribute class whose constructor takes a value of its second type parameter. What
    // that parameter is stands in the type arguments the attribute is applied with, after the
    // first of them, not in the constructor's signature.
    public sealed class PairedAttribute<TFirst, TSecond> : Attribute
    {
        public PairedAttribute(TSecond value, Type type) { }
    }

    [Paired<string, Grain>(Grain.Fine, typeof(Measured))]
    public class ValueOfATypeArgument { }
}
