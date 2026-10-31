using System;
using System.Collections.Generic;
using ExternalLib;

// Attributes on types and members that use a type of this assembly in each way an attribute
// can: as its own class, by a value of an enum, and by a typeof on its own, in an array, of a
// type built from the type, and beside a value of an enum another assembly defines.
namespace ExternalUser.Labels
{
    public enum Rank
    {
        Minor,
        Major,
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class LabelAttribute : Attribute
    {
        public LabelAttribute() { }
        public LabelAttribute(Rank rank) { }
        public LabelAttribute(Rank[] ranks) { }
        public LabelAttribute(Type type) { }
        public LabelAttribute(Type[] types) { }
        public LabelAttribute(Type type, Finish finish) { }
        public LabelAttribute(Type type, DayOfWeek day) { }
        public LabelAttribute(Finish finish) { }

        public Rank Named { get; set; }
        public Type[] Also { get; set; }
        public object Any { get; set; }
    }

    // A value of Rank in each form an argument takes.
    [Label(Rank.Major)]
    public class RankFixed { }

    [Label(Named = Rank.Major)]
    public class RankNamed { }

    [Label(Any = Rank.Major)]
    public class RankAsObject { }

    [Label(new[] { Rank.Major })]
    public class RankInArray { }

    // A value of Rank on each thing a member's attribute can sit on.
    public class RankOnMembers
    {
        [Label(Rank.Major)]
        public int Field;

        [Label(Rank.Major)]
        public int Property { get; set; }

        [Label(Rank.Major)]
        public event EventHandler Event
        {
            add { }
            remove { }
        }

        [Label(Rank.Major)]
        public void Method() { }

        public void Parameter([Label(Rank.Major)] int value) { }

        [return: Label(Rank.Major)]
        public int Return() => 0;

        public void TypeParameter<[Label(Rank.Major)] T>() { }
    }

    public class RankOnTypeParameter<[Label(Rank.Major)] T> { }

    // The class the typeofs below name. An attribute on its own member names it as well, and
    // a type's own members are not among its users.
    public class Subject
    {
        [Label(typeof(Subject))]
        public void Own() { }
    }

    [Label(typeof(Subject))]
    public class TypeofAlone { }

    [Label(Any = typeof(Subject))]
    public class TypeofAsObject { }

    [Label(new[] { typeof(Subject) })]
    public class TypeofInArray { }

    [Label(Also = new[] { typeof(Subject) })]
    public class TypeofInNamedArray { }

    [Label(typeof(List<Subject>))]
    public class TypeofAsTypeArgument { }

    // Finish is ExternalLib's and DayOfWeek the framework's: the underlying type of neither is
    // in this assembly.
    [Label(typeof(Subject), Finish.Gloss)]
    public class TypeofBesideOtherEnum { }

    [Label(typeof(Subject), DayOfWeek.Friday)]
    public class TypeofBesideFrameworkEnum { }

    public class TypeofOnMethods
    {
        [Label(typeof(Subject))]
        public void Alone() { }

        [Label(new[] { typeof(Subject) })]
        public void InArray() { }

        [Label(typeof(List<Subject>))]
        public void AsTypeArgument() { }

        [Label(typeof(Subject), Finish.Gloss)]
        public void BesideOtherEnum() { }
    }

    // The attribute alone, given a value of an enum another assembly defines.
    [Label(Finish.Gloss)]
    public class OtherEnumAlone
    {
        [Label(Finish.Gloss)]
        public void Method() { }
    }
}
