namespace CallShapes
{
    /// <summary>
    /// A virtual method and a virtual property overridden over two levels, and the method also
    /// by a class beside them. C# compiles a call on any of these types as a call to the
    /// declaration in <see cref="Creature"/>, and a call through <c>base.</c> as a call to the
    /// declaration it names.
    /// </summary>
    public class Creature
    {
        public virtual void Die(int cause)
        {
        }

        public virtual int Health { get; set; }
    }

    public class Animal : Creature
    {
        public override void Die(int cause) => base.Die(cause);

        public override int Health
        {
            get => base.Health;
            set => base.Health = value;
        }
    }

    public class Dog : Animal
    {
        public override void Die(int cause) => base.Die(cause + 1);
    }

    /// <summary>Overrides <see cref="Creature.Die"/> beside <see cref="Animal"/>, deriving from neither it nor <see cref="Dog"/>.</summary>
    public class Ghost : Creature
    {
        public override void Die(int cause)
        {
        }
    }

    /// <summary>One call per receiver type, each compiled as a call to the declaration in <see cref="Creature"/>.</summary>
    public class Undertaker
    {
        public void OnCreature(Creature creature) => creature.Die(1);

        public void OnAnimal(Animal animal) => animal.Die(2);

        public void OnDog(Dog dog) => dog.Die(3);

        public void OnGhost(Ghost ghost) => ghost.Die(4);

        public int ReadCreatureHealth(Creature creature) => creature.Health;

        public int ReadAnimalHealth(Animal animal) => animal.Health;

        public void SetDogHealth(Dog dog) => dog.Health = 5;
    }
}
