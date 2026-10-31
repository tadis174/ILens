using System;
using System.IO;

namespace SearchText
{
    /// <summary>What the other classes of this fixture call, so that one pattern finds a line in each.</summary>
    public static class Marker
    {
        public static void Hit(string where) { }
    }

    /// <summary>
    /// Two catch clauses in one method and one in another, so that a pattern has two matching
    /// lines under one member and a third under the next.
    /// </summary>
    public class Reader
    {
        public string Load(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Marker.Hit(ex.Message);
                throw;
            }
        }

        public bool TryParse(string text, out int value)
        {
            try
            {
                value = int.Parse(text);
                return true;
            }
            catch (FormatException)
            {
                value = 0;
                return false;
            }
        }
    }

    /// <summary>
    /// A method whose name is one long run of letters. A pattern that repeats a repeated group
    /// tries every way to split such a run before it gives up, which is how a pattern runs away.
    /// </summary>
    public class Runaway
    {
        public void AMethodWhoseNameIsOneRunOfLettersLongEnoughToMakeABacktrackingPatternRunAway() { }
    }
}
