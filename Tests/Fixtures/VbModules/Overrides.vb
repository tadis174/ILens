Imports System.Collections
Imports System.IO

Namespace VbModules.Overrides
    ' Classes that override members of framework classes, which another assembly defines. Visual
    ' Basic names the override itself in a call on an object of its class, where C# names the
    ' member it overrides, and the decompiled C# writes most of these calls without call syntax.
    Public Class Shelf
        Inherits ArrayList

        Default Public Overrides Property Item(index As Integer) As Object
            Get
                Return Nothing
            End Get
            Set(value As Object)
            End Set
        End Property

        Public Overrides ReadOnly Property Count As Integer
            Get
                Return 0
            End Get
        End Property

        Public Overrides Function GetEnumerator() As IEnumerator
            Return MyBase.GetEnumerator()
        End Function

        Public Overrides Function Add(value As Object) As Integer
            Return 0
        End Function

        ' A read through MyClass, which calls this class's override without virtual dispatch.
        Public Function Size() As Integer
            Return MyClass.Count
        End Function
    End Class

    Public Class Spool
        Inherits MemoryStream

        Public Overrides Property Position As Long
            Get
                Return 0
            End Get
            Set(value As Long)
            End Set
        End Property
    End Class

    ' One caller for each form of call: a property read, an indexer read and write, a For Each, a
    ' collection initializer, a property write and a compound assignment.
    Public Module Users
        Public Function Measure(shelf As Shelf) As Integer
            Return shelf.Count
        End Function

        Public Sub Store(shelf As Shelf)
            shelf(0) = shelf(1)
        End Sub

        Public Function Walk(shelf As Shelf) As Integer
            Dim steps = 0
            For Each item In shelf
                steps += 1
            Next
            Return steps
        End Function

        ' Assigned to a ByRef parameter, so that the decompiler writes a collection initializer
        ' also from a debug build, where it writes separate calls for one that is returned.
        Public Sub Fill(ByRef shelf As Shelf)
            shelf = New Shelf From {1, 2}
        End Sub

        Public Sub Rewind(spool As Spool)
            spool.Position = 0
        End Sub

        Public Sub Advance(spool As Spool)
            spool.Position += 1
        End Sub
    End Module
End Namespace
