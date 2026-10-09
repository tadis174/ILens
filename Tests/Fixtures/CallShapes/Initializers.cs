using System;
using System.Text;

namespace CallShapes
{
    /// <summary>
    /// Fields whose initializers the compiler moves into the constructors: an instance field, a
    /// static one, one whose type needs a using declaration of its own, and one only the
    /// constructor body assigns, which has no initializer. For the list_members tests it also
    /// holds an obsolete member and a nested type.
    /// </summary>
    public class Settings
    {
        private int _retries = 3;

        public static readonly string Label = "settings";

        private readonly StringBuilder _log = new StringBuilder();

        public readonly int Twice;

        public Settings(int value)
        {
            Twice = value * 2;
        }

        public int Retries => _retries;

        [Obsolete("Use Retries.")]
        public int Attempts => _retries;

        public string Log => _log.ToString();

        public sealed class Section
        {
        }
    }
}
