using System;

namespace BaseShift.Sides
{
    // Properties and events only one of the two assemblies has, for the tests on compare asked for
    // one of them by name, which shows its code. BaseShift2 compiles this file with SHIFTED
    // defined: Panel gains Title, Resized and an indexer and loses Caption and Closed, and its Draw
    // raises the event each build has; Frame gains Border, which Panel inherits; and Banner is
    // there alone.
    public class Frame
    {
#if SHIFTED
        public int Border { get; set; }
#endif
    }

    public class Panel : Frame
    {
#if SHIFTED
        public string Title { get; set; }

        public event EventHandler Resized;

        public int this[int column] => column * 2;

        public void Draw() => Resized?.Invoke(this, EventArgs.Empty);
#else
        public string Caption { get; set; }

        public event EventHandler Closed;

        public void Draw() => Closed?.Invoke(this, EventArgs.Empty);
#endif
    }

#if SHIFTED
    public class Banner
    {
        public string Text => "new";
    }
#endif
}
