namespace BaseShift.Namesakes
{
    // Members whose declaration names a class that gives way to a class of the same name in another
    // namespace while something else in it changes as well, for the tests on the lines compare
    // writes in full where a short name would stand for two classes. BaseShift2 compiles this file
    // with SHIFTED defined. In Conditions, Shape's condition names the other Badge and Shape becomes
    // internal, which keeps it one member; Mix names both Badge classes in each build and becomes
    // internal; Keep only becomes internal, and its line stays short. In Retyped, the field Tag, the
    // property Label, the event Changed and the method Take take the other Badge or Handler and
    // become internal, and Swap takes the other Badge and a long, so each is a removal and an
    // addition; the Hold that takes a count takes a long, beside a Hold that names the other Badge
    // in both builds and is not listed, so the lines of Hold stay short. Kinds' field Tag becomes a
    // property of the other Badge, and Limits' constant Max moves to LimitsBase as a constant of the
    // other Level. Fits' Fit, whose condition names the other Badge in each build, returns int in
    // BaseShift2, and Fresh, which only BaseShift2 has, names both Badge classes in one line. Lines
    // whose short name a type shares only with the member's own name or with a condition the line
    // does not show stay short: Palette's constant Mode of the type Mode moves to PaletteBase, and
    // Painted's explicit implementation Color of the type Color takes another body.
    public class Conditions
    {
#if SHIFTED
        internal void Shape<T>(T item) where T : Beta.Badge { }

        internal void Mix<T>(Alpha.Badge badge) where T : Beta.Badge { }

        internal void Keep<T>(T item) where T : Alpha.Badge { }
#else
        public void Shape<T>(T item) where T : Alpha.Badge { }

        public void Mix<T>(Alpha.Badge badge) where T : Beta.Badge { }

        public void Keep<T>(T item) where T : Alpha.Badge { }
#endif
    }

    public class Retyped
    {
#if SHIFTED
        internal Beta.Badge Tag;

        internal Beta.Badge Label { get; set; }

        internal event Beta.Handler Changed;

        internal void Take(Beta.Badge badge) { }

        public void Swap(Beta.Badge badge, long count) { }

        public void Hold(Beta.Badge badge, long count) { }
#else
        public Alpha.Badge Tag;

        public Alpha.Badge Label { get; set; }

        public event Alpha.Handler Changed;

        public void Take(Alpha.Badge badge) { }

        public void Swap(Alpha.Badge badge, int count) { }

        public void Hold(Beta.Badge badge, int count) { }
#endif

        public void Hold(Alpha.Badge badge) { }
    }

    public class Kinds
    {
#if SHIFTED
        public Beta.Badge Tag { get; set; }
#else
        public Alpha.Badge Tag;
#endif
    }

    public class Fits
    {
#if SHIFTED
        public int Fit<T>(Alpha.Badge badge) where T : Beta.Badge => 1;
#else
        public void Fit<T>(Alpha.Badge badge) where T : Beta.Badge { }
#endif
    }

#if SHIFTED
    public class Fresh
    {
        public void Mix<T>(Alpha.Badge badge) where T : Beta.Badge { }
    }
#endif

    public enum Mode
    {
        Slow,
        Fast,
    }

    public class PaletteBase
    {
#if SHIFTED
        public const Mode Mode = BaseShift.Namesakes.Mode.Fast;
#endif
    }

    public class Palette : PaletteBase
    {
#if !SHIFTED
        public const Mode Mode = BaseShift.Namesakes.Mode.Fast;
#endif
    }

    public class Color
    {
    }

    public interface IPainted
    {
        Color Color { get; }
    }

    public class Painted : IPainted
    {
#if SHIFTED
        Color IPainted.Color => null;
#else
        Color IPainted.Color => new Color();
#endif
    }

    public class LimitsBase
    {
#if SHIFTED
        public const Beta.Level Max = Beta.Level.High;
#endif
    }

    public class Limits : LimitsBase
    {
#if !SHIFTED
        public const Alpha.Level Max = Alpha.Level.High;
#endif
    }
}

namespace BaseShift.Namesakes.Alpha
{
    public class Badge
    {
    }

    public enum Level
    {
        Low,
        High,
    }

    public delegate void Handler();
}

namespace BaseShift.Namesakes.Beta
{
    public class Badge
    {
    }

    public enum Level
    {
        Low,
        High,
    }

    public delegate void Handler();
}
