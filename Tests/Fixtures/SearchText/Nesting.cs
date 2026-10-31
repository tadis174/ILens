using System.Runtime.CompilerServices;
using SearchText.Storage;

namespace SearchText
{
    /// <summary>
    /// A class with a nested class, each with a line of its own, for a nested type as the scope,
    /// and a nested class the compiler-generated filter counts as generated. It uses a class of
    /// the sub-namespace, so its decompiled text opens with a using line that names it.
    /// </summary>
    public class Outer
    {
        public void Run()
        {
            Marker.Hit("outer");
            new Shelf().Fill();
        }

        public class Inner
        {
            public void Run()
            {
                Marker.Hit("inner");
            }
        }

        [CompilerGenerated]
        private class Hidden
        {
            public void Run()
            {
                Marker.Hit("hidden");
            }
        }
    }

    /// <summary>A top-level class the compiler-generated filter counts as generated, by its attribute.</summary>
    [CompilerGenerated]
    public class Registry
    {
        public void Run()
        {
            Marker.Hit("registry");
        }
    }
}

namespace SearchText.Storage
{
    /// <summary>A class of a sub-namespace, which a search of the namespace above it does not cover.</summary>
    public class Shelf
    {
        public void Fill()
        {
            Marker.Hit("shelf");
        }
    }
}

namespace Catalog
{
    /// <summary>A class of a second namespace.</summary>
    public class Index
    {
        public void Build()
        {
            SearchText.Marker.Hit("index");
        }
    }

    /// <summary>A second class of that namespace, with nothing the marker pattern matches.</summary>
    public class Entry
    {
        public string Title = "untitled";
    }
}
