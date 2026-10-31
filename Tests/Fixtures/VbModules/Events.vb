Imports System

Namespace VbModules.Events
    ' An event and an auto-implemented property, for the tests on the fields the compiler adds for
    ' them: ChangedEvent holds the event's subscribers and _Size the property's value, each marked
    ' as the compiler's. Ring raises the event, which reads ChangedEvent, and Listener subscribes
    ' to it.
    Public Class Bell
        Public Event Changed As EventHandler

        Public Property Size As Integer

        Public Sub Ring()
            RaiseEvent Changed(Me, EventArgs.Empty)
        End Sub
    End Class

    Public Class Listener
        Public Shared Sub Attach(bell As Bell)
            AddHandler bell.Changed, AddressOf OnChanged
        End Sub

        Private Shared Sub OnChanged(sender As Object, e As EventArgs)
        End Sub
    End Class
End Namespace
