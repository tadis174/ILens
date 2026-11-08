Namespace VbModules.Properties
    ' C# cannot declare a property that takes a parameter and is not its class's default, so the
    ' decompiler writes the getter of this one as a method, with a note of its own above it.
    Public Class Grid
        Public ReadOnly Property Cell(row As Integer) As String
            Get
                Return row.ToString()
            End Get
        End Property
    End Class
End Namespace
