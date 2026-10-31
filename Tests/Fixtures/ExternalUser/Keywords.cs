using ExternalLib;

namespace ExternalUser.Keywords
{
    /// <summary>
    /// Calls methods of ExternalLib that take a parameter by reference, with each keyword C#
    /// has for it. The call says that the parameter is passed by reference; which keyword it
    /// has is in ExternalLib alone.
    /// </summary>
    public class Clerk
    {
        private readonly Vault _vault = new Vault();

        // A property, since what a property uses is asked of its accessors.
        public string First
        {
            get
            {
                _vault.TryTake(0, out string item);
                return item;
            }
        }

        public int Count(Vault vault)
        {
            vault.TryTake(1, out string item);
            int load = 2;
            int total = 3;
            vault.Weigh(in load, ref total);
            return vault.Peek(in load) + total + (item == null ? 0 : 1);
        }
    }
}
