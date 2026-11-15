Namespace VbModules.Calls
    ' An override that its own class calls through MyClass. The compiler calls it without virtual
    ' dispatch, while the decompiled C#, which has nothing like MyClass, reads like a virtual call.
    Public Class Lantern
        Public Overridable Sub Glow()
        End Sub

        Public Overridable Property Light As Integer
            Get
                Return 0
            End Get
            Set(value As Integer)
            End Set
        End Property
    End Class

    Public Class Lamp
        Inherits Lantern

        Public Overrides Sub Glow()
        End Sub

        Public Overrides Property Light As Integer
            Get
                Return 1
            End Get
            Set(value As Integer)
            End Set
        End Property

        Public Sub Shine()
            MyClass.Glow()
        End Sub

        ' A property read and a compound assignment through MyClass, which the decompiled C#
        ' writes as a plain read and a plain increment.
        Public Function Brightness() As Integer
            Return MyClass.Light
        End Function

        Public Sub Brighten()
            MyClass.Light += 1
        End Sub
    End Class
End Namespace
