using System.Reflection.Metadata;
using System.Xml.Linq;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Output;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpyX.Abstractions;
using ICSharpCode.ILSpyX.Settings;

namespace ILens;

/// <summary>
/// Minimal <see cref="ISettingsProvider"/> stub for headless ILSpyX usage.
/// ILSpyX's <c>AssemblyListManager</c> indexes settings by name and expects a
/// non-null XElement for any section; we return a lazily-created child of an
/// in-memory root and never serialize.
/// </summary>
internal sealed class StubSettingsProvider : ISettingsProvider
{
    private readonly XElement _root = new("Settings");

    public XElement this[XName section]
    {
        get
        {
            var el = _root.Element(section);
            if (el == null)
            {
                el = new XElement(section);
                _root.Add(el);
            }
            return el;
        }
    }

    public void Update(Action<XElement> action) => action(_root);
    public void SaveSettings(XElement section) { }
}

/// <summary>
/// Minimal <see cref="ILanguage"/> stub. The ILSpyX analyzers exposed by
/// <see cref="AssemblyHost.RunAnalyzer"/> do not call most of these methods,
/// but the interface must be satisfied for an AnalyzerContext to be constructible.
/// </summary>
internal sealed class StubLanguage : ILanguage
{
    public bool ShowMember(IEntity member) => true;

    /// <summary>
    /// An empty map between methods and the methods the compiler split out of their bodies
    /// (lambdas, local functions, iterator and async state machines).
    /// </summary>
    /// <remarks>
    /// The analyzers ILens runs that ask for the map do so with the handle of a type they
    /// scan. With the empty map they report a use inside generated code under the generated
    /// method, and <see cref="AssemblyHost.AttributeToSource"/> attributes it to source for
    /// every incoming kind alike. Giving them a real map changes no answer and costs time:
    /// "Instantiated By" asks for it for every type in scope. The cost was measured on ILSpyX's
    /// "Read By", which asks the same way and which ILens does not run, since
    /// <see cref="FieldAccessScanner"/> answers that question: 2.3 s on a game's main assembly
    /// with ILSpy's map against 0.9 s without (measured 2026-09-27).
    /// </remarks>
    public CodeMappingInfo GetCodeMappingInfo(MetadataFile module, EntityHandle member)
        => new(module, member.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)member : default);

    public string GetEntityName(MetadataFile module, EntityHandle handle,
        bool fullName, bool omitGenerics)
        => "";

    public string GetTooltip(IEntity entity) => entity.FullName;

    public string TypeToString(IType type, ConversionFlags conversionFlags)
        => conversionFlags.HasFlag(ConversionFlags.UseFullyQualifiedTypeNames) ? type.FullName : type.Name;

    public string EntityToString(IEntity entity, ConversionFlags conversionFlags) => entity.FullName;
}
