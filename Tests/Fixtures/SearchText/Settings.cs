using System;
using System.Runtime.InteropServices;

namespace SearchText
{
    /// <summary>
    /// What stands outside method bodies: an attribute and a base type in the class's header, a
    /// constant, a field with an initial value, an obsolete method, a method with an attribute on
    /// its return value, and a property whose two accessors each hold a line of their own.
    /// </summary>
    [Serializable]
    public class Settings : IDisposable
    {
        public const int Limit = 40;

        public int Retries = 3;

        private int _level;

        public int Level
        {
            get
            {
                Marker.Hit("getter");
                return _level;
            }
            set
            {
                Marker.Hit("setter");
                _level = value;
            }
        }

        [Obsolete("Use Level.")]
        public int OldLevel()
        {
            return _level;
        }

        // A method with an attribute on its return value comes back from the decompiler's writer
        // without a start line, so the search has to take its first line from elsewhere.
        [return: MarshalAs(UnmanagedType.Bool)]
        public bool IsSet()
        {
            Marker.Hit("return attribute");
            return _level > 0;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Created by <see cref="Builder"/> with an object initializer.</summary>
    public class Options
    {
        public string Name;

        public int Size;
    }

    /// <summary>
    /// One statement the decompiler prints over several lines, an object creation with its
    /// initializer, for the tests that a pattern sees one line at a time.
    /// </summary>
    public class Builder
    {
        public Options Make()
        {
            return new Options { Name = "plain", Size = 2 };
        }
    }
}
