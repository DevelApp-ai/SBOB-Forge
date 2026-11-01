using System.Text;
using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// Deterministic hand-rolled PHP source parser (TDS 5.4 Phase A / 7):
/// tokenizer + structural parser producing the language-neutral CodeModel.
/// Pinned as lock grammar "php" (backend "sbobforge-php"). Scope: `<?php`
/// blocks, namespace + use, classes (methods, `#[Attr]` attributes,
/// docblock `@test` / `@requires` markers), top-level functions grouped under
/// a file-stem pseudo-type, param names (`$name` preserved), invocation
/// sites with argument kinds (`$this->m()` receiver "this", `Class::m()`
/// receiver Class, closures' bodies scanned). Double-quoted strings and
/// heredocs containing `$var` are Interpolated; single-quoted are literals;
/// literal `"a" . "b"` concatenation folds. Constructor invocations (`new
/// X()`) are not behavior call sites and are not modeled (mirrors the M2
/// C# parser). Unterminated strings or unbalanced braces are hard errors
/// (TDS 13).
/// </summary>
public sealed class PhpSourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-php";
    public const string Version = "1.0.0";

    string ICodeModelParser.BackendId => BackendId;

    public CodeModel Parse(string relativePath, string sourceText)
    {
        var tokens = Tokenize(sourceText, relativePath);
        return new Parser(tokens, relativePath).ParseFile();
    }

    // ---- tokenizer -------------------------------------------------------

    internal enum Tt { Ident, Var, String, DQString, Heredoc, Number, Op, Comment, Eof }

    internal sealed record Token(Tt Type, string Text, int Line, bool Interp = false);

    private static readonly HashSet<string> s_modifiers = new(StringComparer.Ordinal)
    { "public", "private", "protected", "static", "abstract", "final", "readonly" };

    private static List<Token> Tokenize(string src, string path)
    {
        // PHP code lives inside `<?php … ?>`; anything before it is literal HTML.
        int start = IndexOfPhpOpen(src);
        var toks = new List<Token>();
        if (start < 0)
        {
            toks.Add(new Token(Tt.Eof, "", 1 + src.Count(c => c == '\n')));
            return toks;
        }
        int i = start;
        int line = 1 + src[..start].Count(c => c == '\n');
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (c is ' ' or '\t' or '\r') { i++; continue; }
            if (c == '?' && i + 1 < src.Length && src[i + 1] == '>')
            { i += 2; break; } // close tag: everything after is literal output
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (c == '#' && !(i + 1 < src.Length && src[i + 1] == '['))
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (c == '#' && i + 1 < src.Length && src[i + 1] == '[')
            { toks.Add(new Token(Tt.Op, "#[", line)); i += 2; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                int close = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) throw new ParseHardError($"{path}:{line}: unterminated block comment");
                var body = src[(i + 2)..close];
                toks.Add(new Token(Tt.Comment, body, line));
                line += src[i..(close + 2)].Count(ch => ch == '\n');
                i = close + 2;
                continue;
            }
            if (c == '\'')
            {
                toks.Add(new Token(Tt.String, ReadSingleQuoted(src, ref i, path, line), line));
                continue;
            }
            if (c == '"')
            {
                var (text, interp) = ReadDoubleQuoted(src, ref i, path, line);
                toks.Add(new Token(Tt.DQString, text, line, interp));
                continue;
            }
            if (c == '<' && src[i..].StartsWith("<<<", StringComparison.Ordinal))
            {
                var (text, interp) = ReadHeredoc(src, ref i, path, line);
                toks.Add(new Token(Tt.Heredoc, text, line, interp));
                continue;
            }
            if (c == '$' && i + 1 < src.Length && (char.IsLetter(src[i + 1]) || src[i + 1] == '_'))
            {
                int s = i + 1;
                i++;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                toks.Add(new Token(Tt.Var, "$" + src[s..i], line));
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c is >= '\x80')
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] is >= '\x80')) i++;
                toks.Add(new Token(Tt.Ident, src[s..i], line));
                continue;
            }
            if (char.IsDigit(c))
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '.' || src[i] == '_')) i++;
                toks.Add(new Token(Tt.Number, src[s..i], line));
                continue;
            }
            string three = i + 2 < src.Length ? src.Substring(i, 3) : "";
            if (three is "?->" or "===" or "!==" or "<=>" or "**=" or "<<=" or ">>=" or "??=" or "...")
            { toks.Add(new(Tt.Op, three, line)); i += 3; continue; }
            string two = i + 1 < src.Length ? src.Substring(i, 2) : "";
            if (two is "->" or "::" or "=>" or "==" or "!=" or "<>" or "<=" or ">=" or "&&" or "||"
                or "++" or "--" or "+=" or "-=" or "*=" or "/=" or "%=" or ".=" or "??" or "@=")
            { toks.Add(new(Tt.Op, two, line)); i += 2; continue; }
            toks.Add(new Token(Tt.Op, c.ToString(), line));
            i++;
        }
        toks.Add(new Token(Tt.Eof, "", line));
        return toks;
    }

    private static int IndexOfPhpOpen(string src)
    {
        int idx = src.IndexOf("<?php", StringComparison.OrdinalIgnoreCase);
        int shortEcho = src.IndexOf("<?=", StringComparison.Ordinal);
        if (idx < 0 || (shortEcho >= 0 && shortEcho < idx)) idx = shortEcho;
        return idx < 0 ? -1 : idx + (src[idx..].StartsWith("<?=", StringComparison.Ordinal) ? 3 : 5);
    }

    private static string ReadSingleQuoted(string src, ref int i, string path, int line)
    {
        i++; // opening quote
        var sb = new StringBuilder();
        while (i < src.Length && src[i] != '\'')
        {
            if (src[i] == '\\' && i + 1 < src.Length && (src[i + 1] == '\'' || src[i + 1] == '\\'))
            { sb.Append(src[i + 1]); i += 2; continue; }
            if (src[i] == '\n') throw new ParseHardError($"{path}:{line}: unterminated string literal");
            sb.Append(src[i]); i++;
        }
        if (i >= src.Length) throw new ParseHardError($"{path}:{line}: unterminated string literal");
        i++; // closing quote
        return sb.ToString();
    }

    private static (string Text, bool Interp) ReadDoubleQuoted(string src, ref int i, string path, int line)
    {
        i++; // opening quote
        var sb = new StringBuilder();
        bool interp = false;
        while (i < src.Length && src[i] != '"')
        {
            if (src[i] == '\n') throw new ParseHardError($"{path}:{line}: unterminated string literal");
            if (src[i] == '\\' && i + 1 < src.Length)
            {
                char e = src[i + 1];
                sb.Append(e switch
                {
                    'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0',
                    '\\' => '\\', '"' => '"', '$' => '$', _ => e
                });
                i += 2;
                continue;
            }
            if (src[i] == '$' && i + 1 < src.Length && (char.IsLetter(src[i + 1]) || src[i + 1] == '_'))
                interp = true;
            sb.Append(src[i]); i++;
        }
        if (i >= src.Length) throw new ParseHardError($"{path}:{line}: unterminated string literal");
        i++; // closing quote
        return (sb.ToString(), interp);
    }

    private static (string Text, bool Interp) ReadHeredoc(string src, ref int i, string path, int line)
    {
        i += 3; // <<<
        int lineStart = i;
        while (i < src.Length && src[i] != '\n' && src[i] != ';') i++;
        var labelRaw = src[lineStart..i].Trim().Trim('"', '\'');
        bool literalHeredoc = src[lineStart..i].Contains('\'');
        while (i < src.Length && src[i] != '\n') i++;
        if (i >= src.Length) throw new ParseHardError($"{path}:{line}: unterminated heredoc");
        i++; // past newline
        var sb = new StringBuilder();
        bool interp = false;
        while (true)
        {
            int eol = src.IndexOf('\n', i);
            if (eol < 0) throw new ParseHardError($"{path}:{line}: unterminated heredoc");
            var bodyLine = src[i..eol];
            if (bodyLine.TrimEnd(';', '\r', ' ', '\t') == labelRaw)
            { i = eol + 1; break; }
            if (bodyLine.Contains('$')) interp = true;
            sb.Append(bodyLine).Append('\n');
            i = eol + 1;
        }
        return (sb.ToString(), !literalHeredoc && interp);
    }

    // ---- parser ----------------------------------------------------------

    private sealed class Parser
    {
        private readonly List<Token> _toks;
        private readonly string _path;
        private string? _namespace;
        private int _pos;
        private string? _pendingDocblock;

        public Parser(List<Token> toks, string path) { _toks = toks; _path = path; }

        private Token Cur => _toks[_pos];
        private bool IsOp(string text) => Cur.Type == Tt.Op && Cur.Text == text;
        private bool IsKw(string text) => Cur.Type == Tt.Ident && Cur.Text == text;
        private Token Take() => _toks[_pos++];

        public CodeModel ParseFile()
        {
            var imports = new List<string>();
            var types = new List<TypeDecl>();
            var looseMethods = new List<MethodDecl>();

            while (Cur.Type != Tt.Eof)
            {
                if (Cur.Type == Tt.Comment)
                {
                    _pendingDocblock = Cur.Text;
                    Take();
                    continue;
                }
                if (IsKw("namespace"))
                {
                    Take();
                    var parts = new List<string>();
                    while (Cur.Type is Tt.Ident or Tt.Var && !IsOp(";") && !IsOp("{"))
                    {
                        if (Cur.Type == Tt.Ident) parts.Add(Cur.Text);
                        Take();
                        if (IsOp("\\")) { Take(); continue; }
                        if (IsOp(";") || IsOp("{")) break;
                    }
                    _namespace = string.Join("\\", parts);
                    if (IsOp("{")) SkipBalancedBraces();
                    if (IsOp(";")) Take();
                    continue;
                }
                if (IsKw("use"))
                {
                    Take();
                    if (IsKw("function") || IsKw("const")) Take();
                    var parts = new List<string>();
                    while (Cur.Type != Tt.Eof && !IsOp(";"))
                    {
                        if (Cur.Type == Tt.Ident) parts.Add(Cur.Text);
                        Take();
                    }
                    if (parts.Count > 0) imports.Add(string.Join("\\", parts));
                    if (IsOp(";")) Take();
                    continue;
                }
                if (IsKw("declare"))
                {
                    Take();
                    if (IsOp("(")) SkipBalancedParens();
                    if (IsOp(";")) Take();
                    continue;
                }
                if (Cur.Type == Tt.Ident && s_modifiers.Contains(Cur.Text) || IsKw("class") || IsKw("abstract") || IsKw("final"))
                {
                    var type = TryParseClass();
                    if (type is not null) types.Add(type);
                    continue;
                }
                if (IsKw("function"))
                {
                    looseMethods.Add(ParseFunctionDeclaration(pseudo: true));
                    continue;
                }
                if (Cur.Type == Tt.Op && Cur.Text == "#[")
                {
                    // file-level attributes (e.g. #[AllowDynamicProperties]) — tolerate
                    Take();
                    while (Cur.Type != Tt.Eof && !IsOp("]")) Take();
                    if (IsOp("]")) Take();
                    continue;
                }
                Take(); // tolerate top-level statements, constants, etc.
            }

            if (looseMethods.Count > 0)
            {
                var stem = System.IO.Path.GetFileNameWithoutExtension(_path);
                types.Add(new TypeDecl(stem, _namespace, looseMethods));
            }
            return new CodeModel(_path, types, imports);
        }

        /// <summary>[abstract|final] [modifiers…] class Name [extends Base] [implements A, B] { members }</summary>
        private TypeDecl? TryParseClass()
        {
            while (Cur.Type == Tt.Ident && s_modifiers.Contains(Cur.Text)
                   && !IsKw("function")) // guard: loose `function` after stray modifier
            {
                if (Cur.Text is "class" or "interface" or "trait") break;
                Take();
            }
            if (!IsKw("class") && !IsKw("interface") && !IsKw("trait"))
                return null;
            Take(); // class keyword
            if (Cur.Type != Tt.Ident) { SkipToSemicolon(); return null; }
            int line = Cur.Line;
            string name = Take().Text;

            var bases = new List<string>();
            while (!IsOp("{") && Cur.Type != Tt.Eof)
            {
                if (IsKw("extends") || IsKw("implements")) { Take(); continue; }
                if (Cur.Type == Tt.Ident) bases.Add(Cur.Text);
                Take();
            }
            if (!IsOp("{")) { SkipToSemicolon(); return null; }

            var body = CollectBalanced('{', '}');
            var methods = ParseClassMembers(body, name, line);
            return new TypeDecl(name, _namespace, methods, bases);
        }

        private List<MethodDecl> ParseClassMembers(List<Token> body, string typeName, int classLine)
        {
            var methods = new List<MethodDecl>();
            var attrs = new List<AttributeDecl>();
            string? docblock = _pendingDocblock;
            _pendingDocblock = null;
            int i = 0;
            while (i < body.Count)
            {
                var t = body[i];
                if (t.Type == Tt.Comment)
                {
                    docblock = t.Text;
                    i++;
                    continue;
                }
                if (t.Type == Tt.Op && t.Text == "#[")
                {
                    attrs.AddRange(ParseAttributeList(body, ref i));
                    continue;
                }
                if (t.Type == Tt.Ident && t.Text == "function")
                {
                    methods.Add(ParseFunctionDeclaration(body, ref i, typeName, attrs, docblock));
                    attrs = new List<AttributeDecl>();
                    docblock = null;
                    continue;
                }
                if (t.Type == Tt.Ident && t.Text == "const")
                {
                    i = SkipToSemicolon(body, i);
                    docblock = null;
                    continue;
                }
                // properties / use traits / other members: skip to ';'
                if (t.Type == Tt.Ident && (s_modifiers.Contains(t.Text) || t.Text == "use" || t.Text == "var"))
                {
                    // visibility modifiers directly followed by `function` introduce a method
                    int j = i;
                    while (j < body.Count && body[j].Type == Tt.Ident && s_modifiers.Contains(body[j].Text)) j++;
                    if (j < body.Count && body[j].Type == Tt.Ident && body[j].Text == "function")
                    {
                        i = j;
                        continue;
                    }
                    i = SkipToSemicolon(body, i);
                    docblock = null;
                    continue;
                }
                attrs = new List<AttributeDecl>();
                docblock = null;
                i++;
            }
            return methods;
        }

        private static List<AttributeDecl> ParseAttributeList(List<Token> toks, ref int i)
        {
            var attrs = new List<AttributeDecl>();
            i++; // '#['
            while (i < toks.Count && !(toks[i].Type == Tt.Op && toks[i].Text == "]"))
            {
                var t = toks[i];
                if (t.Type == Tt.Op && t.Text == ",") { i++; continue; }
                if (t.Type != Tt.Ident) { i++; continue; }
                string name = t.Text;
                i++;
                var args = new List<AttributeArg>();
                if (i < toks.Count && toks[i].Type == Tt.Op && toks[i].Text == "(")
                {
                    var inner = CollectBalanced(toks, ref i, '(', ')');
                    foreach (var chunk in SplitTopLevel(inner, ","))
                    {
                        if (chunk.Count == 0) continue;
                        string? named = null;
                        if (chunk.Count >= 3
                            && chunk[0].Type == Tt.Ident
                            && chunk[1].Type == Tt.Op && chunk[1].Text is "=" or ":")
                        {
                            named = chunk[0].Text;
                            chunk.RemoveRange(0, 2);
                        }
                        var arg = ClassifyArg(chunk);
                        args.Add(new AttributeArg(arg.Kind, arg.Value, named));
                    }
                }
                attrs.Add(new AttributeDecl(name, args));
            }
            if (i < toks.Count && toks[i].Type == Tt.Op && toks[i].Text == "]") i++;
            return attrs;
        }

        /// <summary>Docblock markers become attributes: `@test`, `@requires` (PHPUnit docblock cues, TDS 7.2).</summary>
        private static IReadOnlyList<AttributeDecl> DocblockAttributes(string? docblock)
        {
            var attrs = new List<AttributeDecl>();
            if (docblock is null) return attrs;
            foreach (var raw in docblock.Split('\n'))
            {
                var line = raw.Trim('*', ' ', '\t', '\r');
                if (line.StartsWith("@test", StringComparison.Ordinal) && (line.Length == 5 || !char.IsLetterOrDigit(line[5])))
                    attrs.Add(new AttributeDecl("@test", Array.Empty<AttributeArg>()));
                else if (line.StartsWith("@requires", StringComparison.Ordinal))
                    attrs.Add(new AttributeDecl("@requires", Array.Empty<AttributeArg>()));
            }
            return attrs;
        }

        private MethodDecl ParseFunctionDeclaration(bool pseudo) =>
            ParseFunctionDeclaration(_toks, ref _pos, pseudo ? "functions" : "", null, null, topLevel: pseudo);

        private MethodDecl ParseFunctionDeclaration(
            List<Token> toks, ref int i, string typeName,
            IReadOnlyList<AttributeDecl>? declaredAttrs, string? docblock, bool topLevel = false)
        {
            i++; // 'function'
            string name = i < toks.Count && toks[i].Type == Tt.Ident ? toks[i].Text : "<anonymous>";
            int line = i < toks.Count ? toks[i].Line : 0;
            if (i < toks.Count && toks[i].Type == Tt.Ident) i++;
            // `&` reference return
            if (i < toks.Count && toks[i].Type == Tt.Op && toks[i].Text == "&") i++;

            var parameters = new List<string>();
            if (i < toks.Count && toks[i].Type == Tt.Op && toks[i].Text == "(")
            {
                var inner = CollectBalanced(toks, ref i, '(', ')');
                foreach (var chunk in SplitTopLevel(inner, ","))
                {
                    foreach (var t in chunk)
                        if (t.Type == Tt.Var)
                            parameters.Add(t.Text);
                }
            }
            // return type until '{' or ';'
            while (i < toks.Count
                   && !(toks[i].Type == Tt.Op && toks[i].Text == "{")
                   && !(toks[i].Type == Tt.Op && toks[i].Text == ";"))
            {
                i++;
            }
            var attributes = new List<AttributeDecl>(declaredAttrs ?? Array.Empty<AttributeDecl>());
            attributes.AddRange(DocblockAttributes(docblock));

            if (i < toks.Count && toks[i].Type == Tt.Op && toks[i].Text == ";")
            {
                i++; // abstract/interface method
                return new MethodDecl(name, typeName, _namespace, line, parameters, attributes, Array.Empty<InvocationDecl>());
            }
            if (i >= toks.Count || toks[i].Type != Tt.Op || toks[i].Text != "{")
                return new MethodDecl(name, typeName, _namespace, line, parameters, attributes, Array.Empty<InvocationDecl>());
            var body = CollectBalanced(toks, ref i, '{', '}');
            return new MethodDecl(name, typeName, _namespace, line, parameters, attributes, ScanInvocations(body));
        }

        // ---- body scanning ---------------------------------------------------

        internal List<InvocationDecl> ScanInvocations(List<Token> body)
        {
            var invocations = new List<InvocationDecl>();
            int i = 0;
            while (i < body.Count)
            {
                var t = body[i];
                if (t.Type == Tt.Ident && t.Text == "new")
                {
                    // constructors are not behavior call sites (mirrors the M2 C# parser):
                    // skip the class ident and the argument list
                    i++;
                    if (i < body.Count && body[i].Type == Tt.Ident) i++;
                    if (i < body.Count && body[i].Type == Tt.Op && body[i].Text == "(")
                    {
                        int j = i;
                        CollectCallArgs(body, ref j);
                        i = j;
                    }
                    continue;
                }
                if (t.Type == Tt.Ident && t.Text == "function") { i++; continue; } // closure keyword
                if (t.Type == Tt.Ident && i + 1 < body.Count && body[i + 1].Type == Tt.Op && body[i + 1].Text == "(")
                {
                    string? receiver = WalkReceiverChain(body, i);
                    int j = i + 1;
                    var argChunks = CollectCallArgs(body, ref j);
                    invocations.Add(new InvocationDecl(receiver, t.Text, t.Line, argChunks.Select(ClassifyArg).ToList()));
                    foreach (var a in argChunks) invocations.AddRange(ScanInvocations(a));
                    i = j;
                    continue;
                }
                i++;
            }
            return invocations;
        }

        /// <summary>Receiver chain left of the call: `$this->markTestSkipped` → "this", `Class::method` → "Class".</summary>
        private static string? WalkReceiverChain(List<Token> body, int i)
        {
            var chain = new List<string>();
            int k = i - 1;
            while (k >= 0)
            {
                var tk = body[k];
                if (tk.Type == Tt.Op && tk.Text is "->" or "::") { k--; continue; }
                if (tk.Type == Tt.Var) { chain.Insert(0, tk.Text.TrimStart('$')); k--; if (k >= 0 && body[k].Type == Tt.Op && body[k].Text is "->" or "::") continue; break; }
                if (tk.Type == Tt.Ident) { chain.Insert(0, tk.Text); k--; if (k >= 0 && body[k].Type == Tt.Op && body[k].Text is "->" or "::") continue; break; }
                if (tk.Type == Tt.Op && tk.Text == "\\") { k--; continue; }
                break;
            }
            if (chain.Count == 0) return null;
            var joined = string.Join(".", chain);
            return joined.Length == 0 ? null : joined;
        }

        private static List<List<Token>> CollectCallArgs(List<Token> toks, ref int j)
        {
            var args = new List<List<Token>>();
            int depth = 1;
            j++; // past '('
            var current = new List<Token>();
            while (j < toks.Count)
            {
                var tk = toks[j];
                if (tk.Type == Tt.Op && (tk.Text == "(" || tk.Text == "{" || tk.Text == "[")) depth++;
                else if (tk.Type == Tt.Op && (tk.Text == ")" || tk.Text == "}" || tk.Text == "]")) depth--;
                if (depth == 0)
                {
                    if (current.Count > 0) args.Add(current);
                    j++;
                    return args;
                }
                if (depth == 1 && tk.Type == Tt.Op && tk.Text == ",")
                {
                    args.Add(current);
                    current = new List<Token>();
                    j++;
                    continue;
                }
                current.Add(tk);
                j++;
            }
            if (current.Count > 0) args.Add(current);
            return args;
        }

        internal static CallArg ClassifyArg(List<Token> toks)
        {
            if (toks.Count == 0) return new CallArg(ArgKind.Expression, "");
            if (toks.Count == 1)
            {
                var t = toks[0];
                return t.Type switch
                {
                    Tt.String => CallArg.Literal(t.Text),
                    Tt.DQString or Tt.Heredoc => t.Interp
                        ? new CallArg(ArgKind.Interpolated, t.Text)
                        : CallArg.Literal(t.Text),
                    Tt.Number => CallArg.Literal(t.Text),
                    Tt.Var => new CallArg(ArgKind.Identifier, t.Text),
                    Tt.Ident => new CallArg(ArgKind.Identifier, t.Text),
                    _ => new CallArg(ArgKind.Expression, "")
                };
            }
            // parenthesized single expression: ( expr ) → classify inner
            if (toks[^1].Type == Tt.Op && toks[^1].Text == ")" && toks[0].Type == Tt.Op && toks[0].Text == "("
                && TopLevelParenCount(toks) == 1 && SplitTopLevel(toks[1..^1], ",").Count == 1)
                return ClassifyArg(toks[1..^1]);

            // "a" . "b" literal concatenation folds; any $var in the mix → Expression
            var fold = new StringBuilder();
            bool literalConcat = true;
            for (int i = 0; i < toks.Count; i++)
            {
                if (i % 2 == 0)
                {
                    if (toks[i].Type is Tt.String) fold.Append(toks[i].Text);
                    else if (toks[i].Type is Tt.DQString && !toks[i].Interp) fold.Append(toks[i].Text);
                    else { literalConcat = false; break; }
                }
                else if (toks[i].Type != Tt.Op || toks[i].Text != ".") { literalConcat = false; break; }
            }
            if (literalConcat && toks.Count >= 3 && toks.Count % 2 == 1)
                return CallArg.Literal(fold.ToString());

            // `Class::CONST` → Identifier with the constant name
            if (toks.Count == 3 && toks[0].Type == Tt.Ident
                && toks[1].Type == Tt.Op && toks[1].Text == "::"
                && toks[2].Type == Tt.Ident)
                return new CallArg(ArgKind.Identifier, toks[2].Text);

            // array literals: [a, b, …] / array(a, b, …) of literals → List; first element literal → PartialList
            var items = TryArrayItems(toks);
            if (items is not null)
            {
                if (items.All(x => x.Count == 1 && IsLiteralToken(x[0])))
                    return new CallArg(ArgKind.List, string.Join('\u0001', items.Select(x => LiteralText(x[0]))));
                if (items.Count > 0 && items[0].Count == 1 && IsLiteralToken(items[0][0]))
                    return new CallArg(ArgKind.PartialList, LiteralText(items[0][0]));
                return new CallArg(ArgKind.Expression, "");
            }
            return new CallArg(ArgKind.Expression, "");
        }

        private static bool IsLiteralToken(Token t) =>
            t.Type is Tt.String or Tt.Number || (t.Type is Tt.DQString or Tt.Heredoc && !t.Interp);

        private static string LiteralText(Token t) => t.Text;

        /// <summary>[a, b] or array(a, b) items split on top-level commas; null when not an array literal.</summary>
        private static List<List<Token>>? TryArrayItems(List<Token> toks)
        {
            List<Token> inner;
            if (toks.Count >= 3
                && toks[0].Type == Tt.Op && toks[0].Text == "["
                && toks[^1].Type == Tt.Op && toks[^1].Text == "]")
            {
                inner = toks[1..^1];
            }
            else if (toks.Count >= 4
                && toks[0].Type == Tt.Ident && toks[0].Text == "array"
                && toks[1].Type == Tt.Op && toks[1].Text == "("
                && toks[^1].Type == Tt.Op && toks[^1].Text == ")")
            {
                inner = toks[2..^1];
            }
            else return null;
            var items = SplitTopLevel(inner, ",");
            return items;
        }

        private static int TopLevelParenCount(List<Token> toks)
        {
            int depth = 0, count = 0;
            foreach (var t in toks)
            {
                if (t.Type == Tt.Op && t.Text == "(") { depth++; if (depth == 1) count++; }
                else if (t.Type == Tt.Op && t.Text == ")") depth--;
            }
            return count;
        }

        private static int SkipToSemicolon(List<Token> toks, int i)
        {
            while (i < toks.Count && !(toks[i].Type == Tt.Op && toks[i].Text == ";")) i++;
            return i < toks.Count ? i + 1 : i;
        }

        private void SkipToSemicolon()
        {
            while (Cur.Type != Tt.Eof && !IsOp(";")) Take();
            if (IsOp(";")) Take();
        }

        private void SkipBalancedBraces()
        {
            int depth = 0;
            while (Cur.Type != Tt.Eof)
            {
                var t = Take();
                if (t.Type == Tt.Op && t.Text == "{") depth++;
                else if (t.Type == Tt.Op && t.Text == "}")
                {
                    depth--;
                    if (depth == 0) return;
                }
            }
            throw new ParseHardError($"{_path}: unbalanced '{{'");
        }

        private void SkipBalancedParens()
        {
            int depth = 0;
            while (Cur.Type != Tt.Eof)
            {
                var t = Take();
                if (t.Type == Tt.Op && t.Text == "(") depth++;
                else if (t.Type == Tt.Op && t.Text == ")")
                {
                    depth--;
                    if (depth == 0) return;
                }
            }
            throw new ParseHardError($"{_path}: unbalanced '('");
        }

        /// <summary>Collects a balanced region from the parser cursor (which sits on the opening bracket); returns inner tokens and advances past the closing bracket.</summary>
        private List<Token> CollectBalanced(char open, char close)
        {
            int i = _pos;
            var inner = CollectBalanced(_toks, ref i, open, close);
            _pos = i;
            return inner;
        }

        private static List<Token> CollectBalanced(List<Token> toks, ref int i, char open, char close)
        {
            var inner = new List<Token>();
            i++; // opening bracket
            int depth = 1;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Op && t.Text == open.ToString()) { depth++; inner.Add(t); i++; continue; }
                if (t.Type == Tt.Op && t.Text == close.ToString())
                {
                    depth--;
                    if (depth == 0) { i++; return inner; }
                    inner.Add(t); i++; continue;
                }
                inner.Add(t);
                i++;
            }
            throw new ParseHardError($"unbalanced '{open}'");
        }

        internal static List<List<Token>> SplitTopLevel(List<Token> toks, string sep)
        {
            var result = new List<List<Token>>();
            var current = new List<Token>();
            int paren = 0, brace = 0, bracket = 0;
            foreach (var t in toks)
            {
                if (t.Type == Tt.Op)
                {
                    if (t.Text == "(") paren++;
                    else if (t.Text == ")") paren--;
                    else if (t.Text == "{") brace++;
                    else if (t.Text == "}") brace--;
                    else if (t.Text == "[") bracket++;
                    else if (t.Text == "]") bracket--;
                    else if (t.Text == sep && paren == 0 && brace == 0 && bracket == 0)
                    {
                        result.Add(current);
                        current = new List<Token>();
                        continue;
                    }
                }
                current.Add(t);
            }
            if (current.Count > 0) result.Add(current);
            return result;
        }
    }
}
