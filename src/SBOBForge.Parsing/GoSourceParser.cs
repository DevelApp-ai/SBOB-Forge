using System.Text;
using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// Deterministic hand-rolled Go source parser (TDS 5.2 / 7): tokenizer +
/// lightweight structural parser producing the language-neutral CodeModel.
/// Pinned as lock grammar "go119" (backend "sbobforge-go119").
/// Scope: package, imports, top-level func decls (with or without a
/// receiver) grouped under a package pseudo-type, param names, invocation
/// sites with argument kinds. Go has no attributes; test-entry detection is
/// naming-convention based (go test: func TestXxx(t *testing.T)) and lives in
/// the adapter. Type/const/var declarations are tolerated but not modeled.
/// Unterminated strings or unbalanced braces are hard errors (TDS 13).
/// </summary>
public sealed class GoSourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-go119";
    public const string Version = "1.0.0";

    string ICodeModelParser.BackendId => BackendId;

    public CodeModel Parse(string relativePath, string sourceText)
    {
        var tokens = Tokenize(sourceText, relativePath);
        return new Parser(tokens, relativePath).ParseFile();
    }

    // ---- tokenizer -------------------------------------------------------

    private enum Tt { Ident, Keyword, String, RawString, Char, Number, Op, Eof }

    private sealed record Token(Tt Type, string Text, int Line);

    private static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal)
    {
        "break","case","chan","const","continue","default","defer","else","fallthrough",
        "for","func","go","goto","if","import","interface","map","package","range",
        "return","select","struct","switch","type","var"
    };

    private static readonly HashSet<string> s_builtinTypes = new(StringComparer.Ordinal)
    {
        "bool","byte","complex64","complex128","error","float32","float64","int","int8",
        "int16","int32","int64","rune","string","uint","uint8","uint16","uint32","uint64",
        "uintptr","any"
    };

    private static List<Token> Tokenize(string src, string path)
    {
        var toks = new List<Token>();
        int i = 0, line = 1;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (c is ' ' or '\t' or '\r') { i++; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                int close = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) throw new ParseHardError($"{path}:{line}: unterminated block comment");
                line += src[i..(close + 2)].Count(ch => ch == '\n');
                i = close + 2;
                continue;
            }
            if (c == '"')
            {
                toks.Add(new Token(Tt.String, ReadString(src, ref i, ref line, path), line));
                continue;
            }
            if (c == '`')
            {
                int startLine = line;
                i++;
                var sb = new StringBuilder();
                while (i < src.Length && src[i] != '`')
                {
                    if (src[i] == '\n') line++;
                    sb.Append(src[i]);
                    i++;
                }
                if (i >= src.Length) throw new ParseHardError($"{path}:{startLine}: unterminated raw string literal");
                i++;
                toks.Add(new Token(Tt.RawString, sb.ToString(), startLine));
                continue;
            }
            if (c == '\'')
            {
                toks.Add(new Token(Tt.Char, ReadString(src, ref i, ref line, path), line));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                string word = src[s..i];
                toks.Add(new Token(s_keywords.Contains(word) ? Tt.Keyword : Tt.Ident, word, line));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '.' || src[i] == '_'
                                       || ((src[i] == '+' || src[i] == '-') && "eE".Contains(src[i - 1]))))
                {
                    // hex exponent 0x1p-2 keeps its sign; 1e+5 keeps its sign
                    if (src[i] == '+' || src[i] == '-')
                    {
                        i++;
                        continue;
                    }
                    i++;
                    // '_' separators are consumed but skipped; 0x/0b/0o prefixes just scan
                    if (i < src.Length && src[i] == '_') i++;
                }
                // imaginary/hex suffixes (i, x digits already consumed via IsLetterOrDigit)
                toks.Add(new Token(Tt.Number, src[s..i], line));
                continue;
            }
            string three = i + 2 < src.Length ? src.Substring(i, 3) : "";
            string two = i + 1 < src.Length ? src.Substring(i, 2) : "";
            if (three == "..." || three == "&^=" || three == "<<=" || three == ">>=")
            { toks.Add(new(Tt.Op, three, line)); i += 3; continue; }
            if (two is "&^" or "<<" or ">>" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^="
                or "==" or "!=" or "<=" or ">=" or "&&" or "||" or ":=" or "<-" or "++" or "--" or "..")
            { toks.Add(new(Tt.Op, two, line)); i += 2; continue; }
            toks.Add(new Token(Tt.Op, c.ToString(), line));
            i++;
        }
        toks.Add(new Token(Tt.Eof, "", line));
        return toks;
    }

    private static string ReadString(string src, ref int i, ref int line, string path)
    {
        int startLine = line;
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < src.Length && src[i] != '"')
        {
            char c = src[i];
            if (c == '\n') throw new ParseHardError($"{path}:{startLine}: unterminated string literal");
            if (c == '\\')
            {
                if (i + 1 >= src.Length) throw new ParseHardError($"{path}:{startLine}: unterminated string literal");
                char e = src[i + 1];
                sb.Append(e switch
                {
                    'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0',
                    '\\' => '\\', '"' => '"', '\'' => '\'', _ => e
                });
                i += 2;
                continue;
            }
            sb.Append(c);
            i++;
        }
        if (i >= src.Length) throw new ParseHardError($"{path}:{startLine}: unterminated string literal");
        i++; // closing quote
        return sb.ToString();
    }

    // ---- parser ----------------------------------------------------------

    private sealed class Parser
    {
        private readonly List<Token> _toks;
        private readonly string _path;
        private string _pkg = "";
        private int _pos;

        public Parser(List<Token> toks, string path) { _toks = toks; _path = path; }

        private Token Cur => _toks[_pos];
        private bool IsOp(string text) => Cur.Type == Tt.Op && Cur.Text == text;
        private bool IsKw(string text) => Cur.Type == Tt.Keyword && Cur.Text == text;
        private Token Take() => _toks[_pos++];

        public CodeModel ParseFile()
        {
            string pkg = "";
            var imports = new List<string>();
            var methods = new List<MethodDecl>();

            while (Cur.Type != Tt.Eof)
            {
                if (IsKw("package"))
                {
                    Take();
                    if (Cur.Type == Tt.Ident) { _pkg = Take().Text; pkg = _pkg; }
                    continue;
                }
                if (IsKw("import"))
                {
                    Take();
                    if (IsOp("("))
                    {
                        var inner = CollectBalanced('(', ')');
                        foreach (var t in inner)
                            if (t.Type is Tt.String or Tt.RawString)
                                imports.Add(t.Text);
                    }
                    else if (Cur.Type is Tt.String or Tt.RawString)
                    {
                        imports.Add(Take().Text);
                    }
                    continue;
                }
                if (IsKw("func"))
                {
                    methods.Add(ParseFuncDecl());
                    continue;
                }
                if (IsKw("type") || IsKw("const") || IsKw("var"))
                {
                    SkipGroupDecl();
                    continue;
                }
                if (Cur.Type != Tt.Eof) Take();
            }
            var type = new TypeDecl(pkg.Length > 0 ? pkg : "main", null, methods);
            return new CodeModel(_path, new[] { type }, imports);
        }

        /// <summary>type/const/var: skip a grouped ( … ) or braced { … } body, else the rest of the line.</summary>
        private void SkipGroupDecl()
        {
            int startLine = Take().Line;
            while (Cur.Type != Tt.Eof)
            {
                if (IsOp("(")) { SkipBalanced('(', ')'); continue; }
                if (IsOp("{")) { SkipBalanced('{', '}'); continue; }
                if (IsOp("["))
                {
                    // array type/length on the same line: skip balanced brackets
                    SkipBalanced('[', ']');
                    continue;
                }
                if (Cur.Line != startLine) return;
                Take();
            }
        }

        private MethodDecl ParseFuncDecl()
        {
            Take(); // func
            if (IsOp("(")) SkipBalanced('(', ')'); // receiver
            if (Cur.Type != Tt.Ident) { SkipToBodyEnd(); return new MethodDecl("<anonymous>", _pkg, null, Cur.Line, Array.Empty<string>(), Array.Empty<AttributeDecl>(), Array.Empty<InvocationDecl>()); }
            int line = Cur.Line;
            string name = Take().Text;
            SkipGenericArgs();
            var parameters = ParseParamNames();
            // result list: either a type, a (…)-list, or nothing; the body brace follows
            while (!IsOp("{") && Cur.Type != Tt.Eof)
            {
                if (IsOp("(")) { SkipBalanced('(', ')'); continue; }
                Take();
            }
            if (!IsOp("{")) return new MethodDecl(name, _pkg, null, line, parameters, Array.Empty<AttributeDecl>(), Array.Empty<InvocationDecl>());
            var body = CollectBalanced('{', '}');
            return new MethodDecl(name, _pkg, null, line, parameters, Array.Empty<AttributeDecl>(), ScanInvocations(body));
        }

        private void SkipToBodyEnd()
        {
            while (Cur.Type != Tt.Eof && !IsOp("{")) Take();
            if (IsOp("{")) SkipBalanced('{', '}');
        }

        private void SkipGenericArgs()
        {
            if (!IsOp("[")) return;
            int depth = 0;
            while (Cur.Type != Tt.Eof)
            {
                var t = Take();
                if (t.Type == Tt.Op && t.Text == "[") depth++;
                else if (t.Type == Tt.Op && t.Text == "]") { depth--; if (depth == 0) return; }
            }
        }

        /// <summary>Parameter names from a top-level comma-split of the parameter list (TDS 7: names only).</summary>
        private List<string> ParseParamNames()
        {
            var names = new List<string>();
            if (!IsOp("(")) return names;
            var inner = CollectBalanced('(', ')');
            foreach (var chunk in SplitTopLevel(inner, ","))
            {
                if (chunk.Count == 0) continue;
                // strip leading "..." of variadics: consume from the ident run
                var idents = chunk.Where(t => t.Type == Tt.Ident).ToList();
                if (idents.Count == 0) continue; // fully unnamed param (e.g. *testing.T alone)
                if (idents.Count == 1 && s_builtinTypes.Contains(idents[0].Text) && chunk.Count == 1)
                    continue; // unnamed builtin-typed parameter
                if (idents.Count == 1 && chunk.Count == 1)
                {
                    // single ident: a name (named param of a later group type) — keep
                    names.Add(idents[0].Text);
                    continue;
                }
                // "t *testing.T" / "a, b string" (already comma-split → "b string") / "user string":
                // every ident that is followed by a non-ident token (type follows) is a name;
                // the trailing ident run is the type.
                for (int i = 0; i < idents.Count - 1; i++)
                {
                    // a name only when the ident directly precedes a type-ish token (*, ident, [, ...)
                    int idx = chunk.IndexOf(idents[i]);
                    var next = chunk[idx + 1];
                    if (next.Type is Tt.Op && (next.Text == "*" || next.Text == "...") || next.Type == Tt.Ident)
                    {
                        // last ident of the run is the type when it is not itself preceded by a '*'
                        names.Add(idents[i].Text);
                    }
                }
            }
            return names;
        }

        // ---- body scanning ---------------------------------------------------

        internal List<InvocationDecl> ScanInvocations(List<Token> body)
        {
            var invocations = new List<InvocationDecl>();
            int i = 0;
            while (i < body.Count)
            {
                var t = body[i];
                if (t.Type == Tt.Ident && i + 1 < body.Count && body[i + 1].Type == Tt.Op && body[i + 1].Text == "(")
                {
                    string? receiver = null;
                    if (i > 0 && body[i - 1].Type == Tt.Op && body[i - 1].Text == ".")
                        receiver = WalkReceiverChain(body, i);
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

        /// <summary>Dotted receiver chain immediately left of the method name (exec.Command → exec; t.Skip → t).</summary>
        private static string? WalkReceiverChain(List<Token> body, int i)
        {
            var chain = new List<string>();
            int k = i - 1;
            int depth = 0;
            while (k >= 0)
            {
                var tk = body[k];
                if (tk.Type == Tt.Op && tk.Text == ")") { depth++; k--; continue; }
                if (tk.Type == Tt.Op && tk.Text == "(") { depth--; k--; continue; }
                if (tk.Type == Tt.Op && tk.Text == "." && depth == 0) { k--; continue; }
                if (tk.Type == Tt.Ident && depth == 0)
                {
                    chain.Insert(0, tk.Text);
                    if (k - 1 >= 0 && body[k - 1].Type == Tt.Op && body[k - 1].Text == ".") { k--; continue; }
                    break;
                }
                break;
            }
            return chain.Count > 0 ? string.Join(".", chain) : null;
        }

        /// <summary>Collects the argument token chunks of a call whose '(' is at index j. On return j points past the closing paren.</summary>
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

        internal CallArg ClassifyArg(List<Token> toks)
        {
            if (toks.Count == 0) return new CallArg(ArgKind.Expression, "");
            if (toks.Count == 1)
            {
                var t = toks[0];
                return t.Type switch
                {
                    Tt.String or Tt.RawString or Tt.Char or Tt.Number => CallArg.Literal(t.Text),
                    Tt.Keyword when t.Text is "true" or "false" or "nil" => new CallArg(ArgKind.Identifier, t.Text),
                    Tt.Ident => new CallArg(ArgKind.Identifier, t.Text),
                    _ => new CallArg(ArgKind.Expression, "")
                };
            }
            // os.O_* | os.O_* … : flag-mask identifier for os.OpenFile (canonical flag names, '|' joined)
            if (TryGoFlagMask(toks, out var mask)) return new CallArg(ArgKind.Identifier, mask!);

            // dotted identifier chain a.b.c
            if (toks.Count % 2 == 1 && toks.All(t => t.Type == Tt.Ident || (t.Type == Tt.Op && t.Text == ".")))
                return new CallArg(ArgKind.Identifier, string.Join('.', toks.Where(t => t.Type == Tt.Ident).Select(t => t.Text)));

            // string concat of literals: "a" + "b"
            if (IsStringConcat(toks, out string? concat) && concat != null)
                return CallArg.Literal(concat);

            // composite literal []T{…} / [N]T{…} of literals → List; anchored first element → PartialList
            if (TryCompositeItems(toks, out var items))
            {
                if (items.All(x => x.Count == 1 && x[0].Type is Tt.String or Tt.RawString or Tt.Char or Tt.Number))
                    return new CallArg(ArgKind.List, string.Join('\u0001', items.Select(x => x[0].Text)));
                if (items.Count > 0 && items[0].Count == 1
                    && items[0][0].Type is Tt.String or Tt.RawString or Tt.Char or Tt.Number)
                    return new CallArg(ArgKind.PartialList, items[0][0].Text);
            }
            return new CallArg(ArgKind.Expression, "");
        }

        /// <summary>os.O_WRONLY|os.O_CREATE|os.O_APPEND → "O_WRONLY|O_CREATE|O_APPEND" (os. prefix stripped; the flag names are canonical Go constants).</summary>
        private static bool TryGoFlagMask(List<Token> toks, out string? mask)
        {
            mask = null;
            if (toks.Count < 1) return false;
            var parts = new List<string>();
            int i = 0;
            bool expectOr = false;
            while (i < toks.Count)
            {
                if (expectOr)
                {
                    if (toks[i].Type == Tt.Op && toks[i].Text == "|") { i++; expectOr = false; continue; }
                    return false;
                }
                // os.O_FLAG
                if (toks[i].Type == Tt.Ident && toks[i].Text == "os"
                    && i + 2 < toks.Count && toks[i + 1].Type == Tt.Op && toks[i + 1].Text == "."
                    && toks[i + 2].Type == Tt.Ident && toks[i + 2].Text.StartsWith("O_", StringComparison.Ordinal))
                {
                    parts.Add(toks[i + 2].Text);
                    i += 3;
                    expectOr = true;
                    continue;
                }
                return false;
            }
            if (parts.Count == 0) return false;
            mask = string.Join('|', parts);
            return true;
        }

        private static bool IsStringConcat(List<Token> toks, out string? concat)
        {
            concat = null;
            if (toks.Count < 3 || toks.Count % 2 == 0) return false;
            var parts = new List<string>();
            for (int i = 0; i < toks.Count; i++)
            {
                if (i % 2 == 0)
                {
                    if (toks[i].Type is not (Tt.String or Tt.RawString)) return false;
                    parts.Add(toks[i].Text);
                }
                else if (toks[i].Type != Tt.Op || toks[i].Text != "+") return false;
            }
            concat = string.Concat(parts);
            return true;
        }

        /// <summary>Composite literal []T{…} / [N]T{…} / T{…} items split on top-level commas; true only for bracketed/slice forms with items.</summary>
        private static bool TryCompositeItems(List<Token> toks, out List<List<Token>> items)
        {
            items = new List<List<Token>>();
            if (toks.Count < 2) return false;
            int openBrace = toks.FindIndex(t => t.Type == Tt.Op && t.Text == "{");
            if (openBrace < 1 || toks[^1].Type != Tt.Op || toks[^1].Text != "}") return false;
            // require a type prefix ending just before the brace and containing a '[' (slice/array literal)
            var prefix = toks[..openBrace];
            if (!prefix.Any(t => t.Type == Tt.Op && t.Text == "[")) return false;
            items = SplitTopLevel(toks[(openBrace + 1)..^1], ",");
            return items.Count > 0;
        }

        private void SkipBalanced(char open, char close)
        {
            int depth = 0;
            while (Cur.Type != Tt.Eof)
            {
                var t = Take();
                if (t.Type == Tt.Op && t.Text == open.ToString()) depth++;
                else if (t.Type == Tt.Op && t.Text == close.ToString())
                {
                    depth--;
                    if (depth == 0) return;
                }
            }
            throw new ParseHardError($"{_path}: unbalanced '{open}'");
        }

        private List<Token> CollectBalanced(char open, char close)
        {
            var inner = new List<Token>();
            Take(); // opening bracket
            int depth = 1;
            while (Cur.Type != Tt.Eof)
            {
                if (IsOp(open.ToString())) { depth++; inner.Add(Cur); Take(); continue; }
                if (IsOp(close.ToString()))
                {
                    depth--;
                    if (depth == 0) { Take(); return inner; }
                    inner.Add(Cur); Take(); continue;
                }
                inner.Add(Cur); Take();
            }
            throw new ParseHardError($"{_path}: unbalanced '{open}'");
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
