Imports System.Runtime.CompilerServices

Namespace VbModules
    ' A module's extension methods, which the compiler writes into a sealed class that is not
    ' static, for the tests on finding an extension method from the type it extends: one for a
    ' class of this assembly, one for a structure of it passed ByRef, and one for String, a type
    ' of another assembly, beside a method that takes the class without <Extension()>.
    Public Class Gizmo
        Public Size As Integer
    End Class

    Public Structure Dial
        Public Turns As Integer
    End Structure

    Public Module GizmoModule
        <Extension()>
        Public Sub Wave(gizmo As Gizmo)
        End Sub

        <Extension()>
        Public Sub Turn(ByRef dial As Dial)
            dial.Turns += 1
        End Sub

        <Extension()>
        Public Function Shout(text As String) As String
            Return text.ToUpperInvariant()
        End Function

        Public Sub Plain(gizmo As Gizmo)
        End Sub
    End Module

    Public Class Caller
        Public Shared Function Use(gizmo As Gizmo, dial As Dial) As String
            gizmo.Wave()
            dial.Turn()
            Return "quiet".Shout()
        End Function
    End Class
End Namespace
