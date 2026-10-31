using ExternalLib.Defaults;

namespace ExternalUser.Defaults
{
    // A default and a constant of ExternalLib's enum Glaze, and a call that leaves the argument out,
    // for the tests on how a value of an enum another library defines is written: by its member's
    // name where ILens reads that library, and as its number with the enum's name where it does not.
    public class Kiln
    {
        public const Glaze Usual = Glaze.Clear;

        public void Fire(Glaze glaze = Glaze.Frosted) { }

        public void Run() => Fire();
    }
}
