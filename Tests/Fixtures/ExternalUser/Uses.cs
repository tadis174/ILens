using System;
using ExternalLib;

namespace ExternalUser
{
    [Mark(typeof(OnlyInTypeof))]
    public class Consumer
    {
        public Widget Exposed;
        private Widget hidden;

        public void MakeWidget()
        {
            var widget = new Widget(3);
            widget.Paint();
            widget.Paint(2);
            widget.Label = "x";
            var label = widget.Label;
            widget.Size = 1;
            var size = widget.Size;
            widget.Changed += OnChanged;
            Widget.Count++;
            hidden = widget;
        }

        // The use sits in a method the compiler generates for the lambda.
        public Func<int> InLambda() => () => new Widget().Size;

        // One member of a generic type, reached through two constructions of it.
        public void UseGadgets()
        {
            new Gadget<int>().Add(1);
            new Gadget<string>().Add("a");
        }

        public int MakePoint()
        {
            var point = new Point();
            point.X = 1;
            return point.X;
        }

        public void ReleaseHandles()
        {
            Native.Release(IntPtr.Zero);
            Native.Release(UIntPtr.Zero);
        }

        [Mark(Shade.Dark)]
        public void Shaded() { }

        private void OnChanged(object sender, EventArgs e) { }
    }

    // Uses Meter's fields only through the address the compiler takes.
    public class MeterReader
    {
        public string ReadingText(Meter meter) => meter.Reading.ToString();

        public void FillReading(Meter meter) { Fill(out meter.Reading); }

        public void AddReading(Meter meter) { System.Threading.Interlocked.Increment(ref meter.Reading); }

        public void SetPlace(Meter meter) { meter.Place.X = 1; }

        public void MovePlace(Meter meter) { meter.Place.Move(); }

        private static void Fill(out int value) { value = 1; }
    }

    // Its constructor calls the base constructor, which creates no Widget. Its property Size
    // hides Widget's field of that name.
    public class DerivedWidget : Widget
    {
        public DerivedWidget() : base(1) { }

        public new int Size { get; set; }

        public override void Draw() { }
    }

    public class Square : IShape
    {
        public double Area() => 1;
    }

    public interface IBigShape : IShape { }

    public class BigSquare : IBigShape
    {
        public double Area() => 2;
    }

    public class SubSquare : Square { }

    // Implements IShape through Canvas, which nothing in this assembly shows.
    public class MyCanvas : Canvas { }

    public static class WidgetExtensions
    {
        public static void Polish(this Widget widget) { }

        public static void Fill(this Gadget<int> gadget) { }
    }

    [Mark]
    public class Marked
    {
        [Mark]
        public void MarkedMethod() { }

        public void MarkedParameter([Mark] int value) { }
    }

    // The compiler stores [Serializable] as a flag, not as an attribute row.
    [Serializable]
    public class SerializableThing { }
}
