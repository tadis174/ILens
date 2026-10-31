using System;
using ExternalLib;
using ExternalUser.SetByName;

// Fields and properties of attribute classes that attributes set by name, as Text in
// [Note(Text = "x")]. An attribute holds such an argument as the member's name alone, so no
// instruction and no reference row shows that it writes the member, and which class declares
// the member follows from the attribute's class.
[assembly: Note(Text = "assembly")]
[module: Note(Weight = 1)]

namespace ExternalUser.SetByName
{
    public class NoteAttribute : Attribute
    {
        public NoteAttribute() { }

        // Writes the field in code.
        public NoteAttribute(int weight) { Weight = weight; }

        public string Text { get; set; }
        public int Weight;
        public virtual int Order { get; set; }

        // Only attributes set it.
        public string Title { get; set; }

        // Nothing sets it.
        public string Unset { get; set; }
    }

    // Inherits every member, so an attribute of this class sets NoteAttribute's. Its own field
    // named Weight is private, which puts it out of an attribute's reach, so there it hides
    // nothing.
    public class SideNoteAttribute : NoteAttribute
    {
        private new int Weight = 1;

        public int OwnWeight() => Weight;
    }

    // Declares Title anew, so an attribute of this class sets its own.
    public class OwnNoteAttribute : NoteAttribute
    {
        public new string Title { get; set; }
    }

    // Overrides Order. C# binds the name to the declaration in NoteAttribute.
    public class OrderedNoteAttribute : NoteAttribute
    {
        public override int Order { get; set; }
    }

    // An unrelated attribute class with members of the same names.
    public class RemarkAttribute : Attribute
    {
        public string Text { get; set; }
        public int Weight;
    }

    // The property on each thing an attribute can sit on, and once the field beside it.
    [Note(Text = "class", Title = "class")]
    public class Carriers
    {
        [Note(Text = "field", Weight = 2)]
        public int Field;

        [Note(Text = "property")]
        public int Property { get; set; }

        [Note(Text = "method")]
        public void Method() { }

        public void Parameter([Note(Text = "parameter")] int value) { }

        [return: Note(Text = "return")]
        public int Return() => 0;

        // The attribute sits on the method the compiler generates for the lambda.
        public Func<int> OnLambda() => [Note(Text = "lambda")] () => 0;
    }

    [SideNote(Text = "inherited", Weight = 3)]
    public class Inherited { }

    [OwnNote(Title = "own")]
    public class Redeclared { }

    [OrderedNote(Order = 1)]
    public class Overridden { }

    [Remark(Text = "other", Weight = 4)]
    public class Unrelated { }

    public class InCode
    {
        // Sets the property and the field in code and carries no attribute.
        public NoteAttribute Make() => new NoteAttribute { Text = "code", Weight = 5 };

        public string Read(NoteAttribute note) => note.Text;
    }

    // An attribute class whose own property carries the attribute that sets it.
    public class SelfAttribute : Attribute
    {
        [Self(Name = "self")]
        public string Name { get; set; }
    }

    // An attribute class with a type parameter, whose every use names one construction of it.
    public class TypedNoteAttribute<T> : Attribute
    {
        public T Value { get; set; }
    }

    [TypedNote<int>(Value = 1)]
    public class Typed { }

    // An attribute class whose constructor takes a value of ExternalLib's Pitch, which takes
    // one byte, and a type. An attribute's bytes do not say how many bytes the value takes.
    public class SizedAttribute : Attribute
    {
        public SizedAttribute(Pitch pitch, Type type) { }

        public int Level { get; set; }
    }

    // One reading of its bytes, since the name of Carriers has another length than the one
    // below.
    [Sized(Pitch.High, typeof(Carriers), Level = 6)]
    public class LevelSet { }

    // The attribute's bytes spell this class by its full name, which is 58 characters, and end
    // with the name Level and the four bytes of its value. Read with two bytes for the Pitch
    // value where it takes one, the value swallows the name's length; the name's first
    // character, 'E', is then read as the length 69, and 69 characters later two bytes are
    // left, both zero as the upper half of a small number is, which read as "no member set by
    // name". So that reading fits the bytes as well, and sets nothing. Another name, namespace
    // or value ends that.
    public class NamedAtTheLengthWhereALevelIsUnsure { }

    [Sized(Pitch.High, typeof(NamedAtTheLengthWhereALevelIsUnsure), Level = 5)]
    public class LevelSetOrNot { }

    // ExternalLib's TraitAttribute, set through itself and through the classes derived from
    // it, those of ExternalLib and those of this assembly.
    [Trait(Kind = "direct", Rank = 1)]
    public class Direct { }

    [Trait(Caption = "only by name")]
    public class Captioned { }

    [SubTrait(Kind = "through a class of the library")]
    public class ThroughLibraryClass { }

    [OwnTrait(Kind = "the library class's own")]
    public class LibraryRedeclared { }

    public class LocalTraitAttribute : TraitAttribute { }

    [LocalTrait(Kind = "through a class of this assembly")]
    public class ThroughLocalClass { }

    public class LocalOwnTraitAttribute : TraitAttribute
    {
        public new string Kind { get; set; }
    }

    [LocalOwnTrait(Kind = "this assembly's own")]
    public class LocalRedeclared { }

    public class TraitInCode
    {
        // Sets the property in code and carries no attribute.
        public TraitAttribute Make() => new TraitAttribute { Kind = "code" };

        // Sets a property of a class that is no attribute class, which has the name of the one
        // Captioned's attribute sets.
        public Plaque MakePlaque() => new Plaque { Caption = "code" };
    }
}
