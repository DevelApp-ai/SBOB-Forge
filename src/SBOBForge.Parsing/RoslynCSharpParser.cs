using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// C# structural backend for IR v1 (TDS 5.2 implementation deviation, see
/// docs/TDS.md section 5.2 note): the public DevelApp.Minotaur 1.5.0 package
/// tokenizes without punctuation and cannot reconstruct syntax structure, so
/// the canonical C# compiler frontend (Roslyn, pinned in the grammar lock)
/// produces the CodeModel. Minotaur remains the pinned source-validation
/// substrate and the deep-parse backend when a public CognitiveGraph release
/// supports it; this adapter then switches backend only.
/// </summary>
public sealed class RoslynCSharpParser : ICodeModelParser
{
    public string BackendId => "roslyn-csharp";

    public CodeModel Parse(string relativePath, string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();
        var diagnostics = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .OrderBy(d => d.Location.SourceSpan.Start, Comparer<int>.Default)
            .ToList();
        if (diagnostics.Count > 0)
            throw new ParseHardError(
                $"{relativePath}: C# parse failed at line {diagnostics[0].Location.GetLineSpan().StartLinePosition.Line + 1}: {diagnostics[0].GetMessage()}");

        var types = new List<TypeDecl>();
        foreach (var nsDecl in root.Members.OfType<NamespaceDeclarationSyntax>())
        {
            var nsName = nsDecl.Name.ToString();
            types.AddRange(nsDecl.Members.SelectMany(m => CollectTypes(m, nsName)));
        }
        // File-scoped namespaces and global types without namespace
        foreach (var fileNs in root.Members.OfType<FileScopedNamespaceDeclarationSyntax>())
            types.AddRange(fileNs.Members.SelectMany(m => CollectTypes(m, fileNs.Name.ToString())));
        foreach (var m in root.Members)
            if (m is not (NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax))
                types.AddRange(CollectTypes(m, null));

        return new CodeModel(relativePath, types);
    }

    private static IEnumerable<TypeDecl> CollectTypes(MemberDeclarationSyntax member, string? ns)
    {
        var nested = member switch
        {
            NamespaceDeclarationSyntax n => n.Members.SelectMany(x => CollectTypes(x, ns ?? n.Name.ToString())),
            ClassDeclarationSyntax c => new[] { ToTypeDecl(c, ns) },
            StructDeclarationSyntax s => new[] { ToTypeDecl(s, ns) },
            RecordDeclarationSyntax r => new[] { ToTypeDecl(r, ns) },
            _ => Enumerable.Empty<TypeDecl>()
        };
        return nested;
    }

    private static TypeDecl ToTypeDecl(TypeDeclarationSyntax type, string? ns)
    {
        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Select(m => ToMethodDecl(m, type.Identifier.ValueText, ns))
            .ToList();
        return new TypeDecl(type.Identifier.ValueText, ns, methods);
    }

    private static MethodDecl ToMethodDecl(MethodDeclarationSyntax m, string typeName, string? ns)
    {
        var line = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var attrs = m.AttributeLists
            .SelectMany(l => l.Attributes)
            .Select(a => new AttributeDecl(
                StripAttributeSuffix(a.Name.ToString()),
                a.ArgumentList?.Arguments.Select(ToAttrArg).ToList() ?? new List<AttributeArg>()))
            .ToList();
        var parameters = m.ParameterList.Parameters
            .Select(p => p.Identifier.ValueText)
            .ToList();

        var invocations = m.Body is null
            ? (m.ExpressionBody is null
                ? new List<InvocationDecl>()
                : CollectInvocations(m.ExpressionBody.Expression))
            : CollectInvocations(m.Body);

        return new MethodDecl(m.Identifier.ValueText, typeName, ns, line, parameters, attrs, invocations);
    }

    private static string StripAttributeSuffix(string name) =>
        name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length
            ? name[..^"Attribute".Length]
            : name;

    private static AttributeArg ToAttrArg(AttributeArgumentSyntax a)
    {
        var name = a.NameEquals?.Name.ToString();
        return a.Expression switch
        {
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression)
                => new AttributeArg(ArgKind.Literal, lit.Token.ValueText, name),
            LiteralExpressionSyntax num => new AttributeArg(ArgKind.Literal, num.Token.ValueText, name),
            InterpolatedStringExpressionSyntax => new(ArgKind.Interpolated, a.ToString(), name),
            _ => new(ArgKind.Expression, a.ToString(), name)
        };
    }

    private static List<InvocationDecl> CollectInvocations(SyntaxNode node)
    {
        var result = new List<InvocationDecl>();
        // DescendantNodesAndSelf: an expression-bodied method's arrow expression can
        // *be* the invocation itself, which DescendantNodes would skip.
        foreach (var inv in node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            string? receiver = null;
            string method;
            switch (inv.Expression)
            {
                case MemberAccessExpressionSyntax ma:
                    receiver = UnwrapReceiver(ma.Expression);
                    method = ma.Name.Identifier.ValueText;
                    break;
                case IdentifierNameSyntax id:
                    method = id.Identifier.ValueText;
                    break;
                case MemberBindingExpressionSyntax mb: // inside conditional access a?.M()
                    receiver = null;
                    method = mb.Name.Identifier.ValueText;
                    break;
                default:
                    receiver = inv.Expression.ToString();
                    method = inv.Expression.ToString();
                    break;
            }
            var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var args = inv.ArgumentList.Arguments
                .Select(ToCallArg)
                .ToList();
            result.Add(new InvocationDecl(receiver, method, line, args));
        }

        // Constructors surface as invocations of the type name (e.g. new TcpListener(...))
        // so catalog rules can match construction sites; receivers are null to keep
        // member calls and constructions distinguishable.
        foreach (var ctor in node.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>())
        {
            if (ctor.Type is not IdentifierNameSyntax id) continue;
            var line = ctor.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var args = ctor.ArgumentList?.Arguments.Select(ToCallArg).ToList() ?? new List<CallArg>();
            result.Add(new InvocationDecl(null, id.Identifier.ValueText, line, args));
        }
        return result;
    }

    /// <summary>Receiver display: identifier chains of static class references
    /// (System.Diagnostics.Process → Process); member-chain root text otherwise (a.B.C → A; GetType() → Type).</summary>
    private static string? UnwrapReceiver(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax ma when IsIdentifierChain(ma) => ma.Name.Identifier.ValueText,
        MemberAccessExpressionSyntax ma => UnwrapReceiver(ma.Expression) ?? ma.Expression.ToString(),
        PredefinedTypeSyntax p => p.Keyword.ValueText,
        ThisExpressionSyntax => "this",
        InvocationExpressionSyntax inv => inv.Expression is MemberAccessExpressionSyntax m2
            ? m2.Name.Identifier.ValueText
            : inv.Expression.ToString(),
        ObjectCreationExpressionSyntax oc => oc.Type.ToString(),
        CastExpressionSyntax c => UnwrapReceiver(c.Expression),
        ParenthesizedExpressionSyntax pe => UnwrapReceiver(pe.Expression),
        _ => expr.ToString()
    };

    /// <summary>True when the expression is a pure dotted identifier chain (a namespace-qualified type reference).</summary>
    private static bool IsIdentifierChain(MemberAccessExpressionSyntax ma)
    {
        var e = (ExpressionSyntax)ma;
        while (true)
        {
            if (e is IdentifierNameSyntax) return true;
            if (e is not MemberAccessExpressionSyntax inner) return false;
            e = inner.Expression;
        }
    }

    private static CallArg ToCallArg(ArgumentSyntax a) => a.Expression switch
    {
        LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression)
            => CallArg.Literal(lit.Token.ValueText),
        LiteralExpressionSyntax litNum => CallArg.Literal(litNum.Token.ValueText),
        InterpolatedStringExpressionSyntax => new CallArg(ArgKind.Interpolated, a.ToString()),
        IdentifierNameSyntax id => new CallArg(ArgKind.Identifier, id.Identifier.ValueText),
        MemberAccessExpressionSyntax ma => new CallArg(ArgKind.Identifier, ma.ToString()),
        BinaryExpressionSyntax be when be.IsKind(SyntaxKind.AddExpression)
            => FoldConcat(be),
        InvocationExpressionSyntax inv => new CallArg(ArgKind.Expression, inv.ToString()),
        _ => new CallArg(ArgKind.Expression, a.ToString())
    };

    /// <summary>Constant-fold string concatenations of literals ("a" + "b" → "ab", TDS 7.3 tier L).</summary>
    private static CallArg FoldConcat(BinaryExpressionSyntax be)
    {
        var parts = new List<string>();
        var allLiteral = true;
        void Collect(ExpressionSyntax e)
        {
            switch (e)
            {
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression):
                    Collect(b.Left); Collect(b.Right);
                    break;
                case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                    parts.Add(lit.Token.ValueText);
                    break;
                default:
                    allLiteral = false;
                    break;
            }
        }
        Collect(be.Left); Collect(be.Right);
        return allLiteral ? CallArg.Literal(string.Concat(parts)) : new CallArg(ArgKind.Expression, be.ToString());
    }
}
