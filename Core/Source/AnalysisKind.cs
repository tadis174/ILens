namespace ILens;

/// <summary>
/// Cross-reference analysis kinds exposed by the analyze tool. Each value but
/// <see cref="CallSites"/> maps to an ILSpyX analyzer header (see <see cref="AnalysisDispatch"/>),
/// and each is valid only for a subset of symbol categories — e.g. <see cref="ReadBy"/> applies
/// only to fields and properties.
/// The schema enum surfaces the closed set to the agent at tool-listing time.
/// </summary>
public enum AnalysisKind
{
    UsedBy,
    InstantiatedBy,
    ExposedBy,
    ExtensionMethods,
    AppliedTo,
    OverriddenBy,
    ImplementedBy,
    Uses,
    Implements,
    ReadBy,
    AssignedBy,
    CallSites,
}

/// <summary>
/// Coarse classification of a resolved symbol — used for routing analysis kinds
/// to the symbols they accept. Mirrors the five symbol categories the analyze tool handles.
/// </summary>
public enum SymbolCategory
{
    Type,
    Method,
    Property,
    Field,
    Event,
}
