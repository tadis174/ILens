using System;
using ExternalLib;
using ExternalUser;

// Attributes on the assembly and on the module, which belong to no type or member: one of
// ExternalLib's and one of this assembly's own on each, every one naming a class nothing else
// names. The fifth names its class beside a value of an enum ExternalLib defines. The last
// names its two classes after a value of Grade, an enum of this assembly that takes one byte
// where most enums take four.
[assembly: Scope(typeof(OnlyOnAssembly))]
[module: Scope(typeof(OnlyOnModule))]
[assembly: LocalScope(typeof(LocalOnAssembly))]
[module: LocalScope(typeof(LocalOnModule))]
[assembly: LocalScope(typeof(LocalBesideEnum), Tint.Deep)]
[module: LocalScope(Level = Grade.High, Also = new[] { typeof(LocalAfterEnum), typeof(OnlyAfterEnum) })]

namespace ExternalUser
{
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Module, AllowMultiple = true)]
    public sealed class LocalScopeAttribute : Attribute
    {
        public LocalScopeAttribute() { }
        public LocalScopeAttribute(Type type) { }
        public LocalScopeAttribute(Type type, Tint tint) { }

        public Grade Level { get; set; }
        public Type[] Also { get; set; }
    }

    public enum Grade : byte
    {
        Low,
        High,
    }

    public class LocalOnAssembly { }

    public class LocalOnModule { }

    public class LocalBesideEnum { }

    public class LocalAfterEnum { }
}
