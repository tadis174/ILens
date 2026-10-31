using System;
using System.Collections.Generic;

namespace CallShapes.Ranks
{
    /// <summary>
    /// Overloads that differ in an array's number of dimensions alone, which C# writes with a comma
    /// for each dimension past the first.
    /// </summary>
    public class Grid
    {
        public int Take(int[] values) => 1;

        public int Take(int[,] values) => 2;

        public int Take(int[,,] values) => 3;
    }

    /// <summary>
    /// Overloads that take an array of arrays, which C# writes with the outermost array's brackets
    /// first: int[][,] is an array of two-dimensional arrays and int[,][] a two-dimensional array of
    /// arrays. Reflection writes the brackets the other way round.
    /// </summary>
    public class Layers
    {
        public int Put(int[][,] cells) => 1;

        public int Put(int[,][] cells) => 2;

        public int Put(int[][] cells) => 3;
    }

    /// <summary>
    /// A two-dimensional array in each place a type stands: a constructor's parameter, a field, a
    /// property, an event's and a delegate's type, an indexer's index beside an indexer on an array
    /// of one dimension, a return type, a type argument, a parameter passed by reference beside one
    /// that takes an array of one dimension, and an array whose element is a nullable value; and
    /// methods that read and write an element.
    /// </summary>
    public class Board
    {
        public Board() { }

        public Board(int[,] cells) { Cells = cells; }

        public int[,] Cells;

        public int[,] Marks { get; set; }

        public event Action<int[,]> Changed { add { } remove { } }

        public delegate int[,] Maker(int[,] seed);

        public int this[int[,] key] => 2;

        public int this[int[] key] => 1;

        public int[,] Make(int width) => new int[width, width];

        public List<int[,]> Rows(List<int[]> flat) => null;

        public void Fill(ref int[,] cells) { }

        public void Fill(ref int[] cells) { }

        public int Count(int?[,] cells) => 0;

        public int Read(int[,] cells) => cells[0, 0];

        public void Write(int[,] cells) => cells[0, 0] = 1;
    }

    /// <summary>A class's type parameter as the element of an array of one and of two dimensions.</summary>
    public class Shelf<T>
    {
        public int Take(T[] values) => 1;

        public int Take(T[,] values) => 2;
    }

    /// <summary>Calls each overload of <see cref="Grid"/>, <see cref="Layers"/> and <see cref="Shelf{T}"/>.</summary>
    public class Player
    {
        public int Play(Grid grid) => grid.Take(new int[1]) + grid.Take(new int[1, 1]) + grid.Take(new int[1, 1, 1]);

        public int Stack(Layers layers) => layers.Put(new int[1][,]) + layers.Put(new int[1, 1][]) + layers.Put(new int[1][]);

        public int Shelve(Shelf<string> shelf) => shelf.Take(new string[1]) + shelf.Take(new string[1, 1]);
    }
}
