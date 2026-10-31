using System;
using System.Collections.Generic;

// An attribute class with type parameters whose constructor takes a value of its second one.
// To read that value, a reader has to get past the first type argument, and the decoder of the
// metadata library fails there when that argument is a class, a struct, an enum or a
// constructed type. Each of the four stands first here once. Every application names a class
// by typeof after the value, which only a reader that got past the value finds.
namespace ExternalUser.Generics
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class OrderedAttribute<TFirst, TSecond> : Attribute
    {
        public OrderedAttribute(TSecond value, Type about) { }
    }

    // The class the typeofs below name.
    public class Topic { }

    // The types that stand as the first type argument.
    public class Lead { }
    public struct Stretch { }
    public enum Phase { Early, Late }

    [Ordered<Lead, int>(1, typeof(Topic))]
    public class AfterAClass { }

    [Ordered<Stretch, int>(2, typeof(Topic))]
    public class AfterAStruct { }

    [Ordered<Phase, int>(3, typeof(Topic))]
    public class AfterAnEnum { }

    [Ordered<List<int>, int>(4, typeof(Topic))]
    public class AfterAConstructedType { }

    // The value is of an enum another assembly defines, so the attribute's bytes do not say
    // how many of them it takes.
    [Ordered<List<Lead>, DateTimeKind>(DateTimeKind.Utc, typeof(Topic))]
    public class AfterAConstructedTypeWithAnEnumValue { }

    // A class of another assembly named after the value.
    [Ordered<Lead, int>(5, typeof(Uri))]
    public class NamesALibraryClass { }
}
