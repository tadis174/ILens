Namespace VbModules.Calls
    ' An override that its own class calls through MyClass. The compiler calls it without virtual
    ' dispatch, while the decompiled C#, which has nothing like MyClass, reads like a virtual call.
    Public Class Lantern
        Public Overridable Sub Glow()
        End Sub
    End Class

    Public Class Lamp
        Inherits Lantern

        Public Overrides Sub Glow()
        End Sub

        Public Sub Shine()
            MyClass.Glow()
        End Sub
    End Class
End Namespace
