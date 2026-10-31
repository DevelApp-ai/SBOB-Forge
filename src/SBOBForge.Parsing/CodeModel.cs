namespace SBOBForge.Parsing;

/// <summary>
/// Language-neutral code model: the small set of facts the pipeline needs
/// (TDS 6-7): types, test-entry candidates (methods + attributes), and
/// invocation sites with argument-value kinds. Independent of any parser
/// backend; Minotaur CognitiveGraph parsing lands here when the public
/// package supports it (TDS 5.2 upgrade path).
/// </summary>
public sealed record CodeModel(
    string RelativePath,
    IReadOnlyList<TypeDecl> Types)
{
    public IEnumerable<MethodDecl> AllMethods => Types.SelectMany(t => t.Methods);
}

public sealed record TypeDecl(
    string Name,
    string? Namespace,
    IReadOnlyList<MethodDecl> Methods);

public sealed record MethodDecl(
    string Name,
    string DeclaringType,
    string? Namespace,
    int Line,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<AttributeDecl> Attributes,
    IReadOnlyList<InvocationDecl> Invocations)
{
    public string QualifiedName =>
        (string.IsNullOrEmpty(Namespace) ? "" : Namespace + ".") + DeclaringType + "." + Name;
}

public sealed record AttributeDecl(
    string Name,
    IReadOnlyList<AttributeArg> Args);

/// <summary>Attribute argument; only literals are resolvable at lift time.</summary>
public sealed record AttributeArg(ArgKind Kind, string Value, string? Name = null)
{
    public bool IsLiteral => Kind == ArgKind.Literal;

    /// <summary>Named argument (e.g. Skip=, Ignore=) with this name.</summary>
    public bool IsNamed(string name) => string.Equals(Name, name, StringComparison.Ordinal);
}

public sealed record InvocationDecl(
    string? Receiver,
    string Method,
    int Line,
    IReadOnlyList<CallArg> Args);

/// <summary>Argument value classification for tier assignment (TDS 7.3).</summary>
public sealed record CallArg(ArgKind Kind, string Value)
{
    public static CallArg Literal(string v) => new(ArgKind.Literal, v);
    public bool IsLiteral => Kind == ArgKind.Literal;
}

public enum ArgKind
{
    /// <summary>Constant string/numeric literal (no interpolation).</summary>
    Literal,

    /// <summary>Interpolated string ($"..."): never literal.</summary>
    Interpolated,

    /// <summary>Identifier or expression: needs constant folding or placeholder policy.</summary>
    Identifier,

    /// <summary>Anything else (await, lambdas, complex expressions): quarantine.</summary>
    Expression
}
