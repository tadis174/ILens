using System.ComponentModel;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace ILens.Tools;

[McpServerToolType]
public static class SummarizeTypeTool
{
    [McpServerTool(Name = "summarize_type", ReadOnly = true),
     Description("Get the public and protected API surface of a type — signatures only, no method bodies. " +
        "Use this for quick lookups of available members, fields, and properties.")]
    public static string SummarizeType(
        AssemblyHostRegistry registry,
        [Description("Path to the .NET assembly to inspect (must be under an allowed root).")] string assembly,
        [Description("Fully qualified type name, e.g. 'System.String' or 'System.IO.File'.")] string typeName)
    {
        var host = registry.GetOrLoad(assembly);
        var type = host.Resolver.ResolveType(typeName);
        var tree = host.DecompileTypeSyntaxTree(type);

        // Walk the AST and strip non-public members and method bodies
        var visitor = new SummaryVisitor();
        tree.AcceptVisitor(visitor);

        return tree.ToString();
    }

    /// <summary>
    /// AST visitor that removes non-public members and method bodies in-place.
    /// </summary>
    private sealed class SummaryVisitor : DepthFirstAstVisitor
    {
        public override void VisitMethodDeclaration(MethodDeclaration node)
        {
            if (!IsInsideInterface(node) && !IsPublicOrProtected(node.Modifiers))
            {
                node.Remove();
                return;
            }
            // Strip body, replace with semicolon
            node.Body = BlockStatement.Null;
            base.VisitMethodDeclaration(node);
        }

        public override void VisitConstructorDeclaration(ConstructorDeclaration node)
        {
            if (!IsPublicOrProtected(node.Modifiers))
            {
                node.Remove();
                return;
            }
            node.Body = BlockStatement.Null;
            base.VisitConstructorDeclaration(node);
        }

        public override void VisitPropertyDeclaration(PropertyDeclaration node)
        {
            if (!IsInsideInterface(node) && !IsPublicOrProtected(node.Modifiers))
            {
                node.Remove();
                return;
            }
            // Simplify accessor bodies
            foreach (var accessor in node.Children.OfType<Accessor>())
            {
                accessor.Body = BlockStatement.Null;
            }
            base.VisitPropertyDeclaration(node);
        }

        public override void VisitFieldDeclaration(FieldDeclaration node)
        {
            if (!IsInsideInterface(node) && !IsPublicOrProtected(node.Modifiers))
            {
                node.Remove();
                return;
            }
            base.VisitFieldDeclaration(node);
        }

        public override void VisitEventDeclaration(EventDeclaration node)
        {
            if (!IsInsideInterface(node) && !IsPublicOrProtected(node.Modifiers))
            {
                node.Remove();
                return;
            }
            base.VisitEventDeclaration(node);
        }

        public override void VisitOperatorDeclaration(OperatorDeclaration node)
        {
            if (!IsInsideInterface(node) && !IsPublicOrProtected(node.Modifiers))
            {
                node.Remove();
                return;
            }
            node.Body = BlockStatement.Null;
            base.VisitOperatorDeclaration(node);
        }

        public override void VisitIndexerDeclaration(IndexerDeclaration node)
        {
            if (!IsInsideInterface(node) && !IsPublicOrProtected(node.Modifiers))
            {
                node.Remove();
                return;
            }
            foreach (var accessor in node.Children.OfType<Accessor>())
            {
                accessor.Body = BlockStatement.Null;
            }
            base.VisitIndexerDeclaration(node);
        }

        public override void VisitDestructorDeclaration(DestructorDeclaration node)
        {
            node.Remove();
        }

        private static bool IsPublicOrProtected(Modifiers modifiers)
        {
            return (modifiers & (Modifiers.Public | Modifiers.Protected)) != 0;
        }

        /// <summary>
        /// Interface members have no explicit access modifiers (implicitly public),
        /// so the modifier check alone would strip them. Check the parent node.
        /// </summary>
        private static bool IsInsideInterface(AstNode node)
        {
            return node.Parent is TypeDeclaration td && td.ClassType == ClassType.Interface;
        }
    }
}
