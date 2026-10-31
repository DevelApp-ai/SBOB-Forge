using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// Deterministic hand-rolled Python source parser (TDS 5.2 / 7): indent-aware
/// tokenizer + lightweight structural parser producing the language-neutral
/// CodeModel. Pinned as lock grammar "python311" (backend "sbobforge-python311").
/// Scope: module-level functions (grouped under a module pseudo-type), classes
/// (name, bases), methods (name, line, params, decorators incl. arguments),
/// invocation sites with argument kinds. Module-level statements are not
/// modeled (no declaration context). Unterminated strings or bad indentation
/// are hard errors (invalid input, TDS 13).
/// </summary>
public sealed class PythonSourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-python311";
    public const string Version = "1.0.0";

    string ICodeModelParser.BackendId => BackendId;

    public CodeModel Parse(string relativePath, string sourceText)
    {
        var tokens = Tokenize(sourceText, relativePath);
        return new Parser(tokens, relativePath).ParseModule();
    }

    // ---- tokenizer -------------------------------------------------------

    private enum Tt { Name, Str, FStr, Number, Op, Newline, Indent, Dedent, Eof }

    private sealed record Token(Tt Type, string Text, int Line);

    private static List<Token> Tokenize(string src, string path)
    {
        var toks = new List<Token>();
        var indents = new List<int> { 0 };
        int i = 0, line = 1, bracketDepth = 0;

        while (i < src.Length)
        {
            // ---- start of a logical line: indentation handling ----
            int col = 0;
            bool sawSpace = false, sawTab = false;
            while (i < src.Length && (src[i] == ' ' || src[i] == '\t'))
            {
                if (src[i] == '\t') { sawTab = true; col = (col / 8 + 1) * 8; }
                else { sawSpace = true; col++; }
                i++;
            }
            if (sawSpace && sawTab)
                throw new ParseHardError($"{path}:{line}: inconsistent use of tabs and spaces in indentation");
            if (i >= src.Length || src[i] == '\n' || src[i] == '\r' || src[i] == '#')
            {
                // blank or comment-only line: no indent events, no NEWLINE token
                while (i < src.Length && src[i] != '\n') i++;
                if (i < src.Length) { i++; line++; }
                continue;
            }
            if (bracketDepth == 0)
            {
                if (col > indents[^1])
                {
                    indents.Add(col);
                    toks.Add(new Token(Tt.Indent, "", line));
                }
                else if (col < indents[^1])
                {
                    while (indents[^1] > col)
                    {
                        indents.RemoveAt(indents.Count - 1);
                        toks.Add(new Token(Tt.Dedent, "", line));
                    }
                    if (indents[^1] != col)
                        throw new ParseHardError($"{path}:{line}: unindent does not match any outer indentation level");
                }
            }

            // ---- tokenize physical line content ----
            bool continuation = false;
            while (i < src.Length && src[i] != '\n')
            {
                char c = src[i];
                if (c == '\r') { i++; continue; }
                if (c == ' ' || c == '\t') { i++; continue; }
                if (c == '#')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    break;
                }
                if (c == '\\' && i + 1 < src.Length && src[i + 1] == '\n')
                {
                    // line continuation
                    i++;
                    line++;
                    i++;
                    continuation = true;
                    break;
                }
                // string with optional prefix
                string? prefix = ReadPrefix(src, ref i);
                if (i < src.Length && (src[i] == '"' || src[i] == '\''))
                {
                    var (kind, text) = ReadPyString(src, ref i, ref line, path, prefix ?? "");
                    toks.Add(new Token(kind, text, line));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                    toks.Add(new Token(Tt.Name, src[s..i], line));
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
                {
                    int s = i;
                    while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '.' || src[i] == '_')) i++;
                    toks.Add(new Token(Tt.Number, src[s..i], line));
                    continue;
                }
                string two = i + 1 < src.Length ? src.Substring(i, 2) : "";
                if (two is "**" or "//" or "==" or "!=" or "<=" or ">=" or "->" or "+=" or "-=" or "*=" or "/=" or "%=" or "//" or "<<" or ">>" or "&=" or "|=" or "^=" or ":=" or "@=")
                {
                    toks.Add(new Token(Tt.Op, two, line));
                    i += 2;
                    continue;
                }
                if (c is '(' or '[' or '{') bracketDepth++;
                else if (c is ')' or ']' or '}') bracketDepth = Math.Max(0, bracketDepth - 1);
                toks.Add(new Token(Tt.Op, c.ToString(), line));
                i++;
            }
            if (i < src.Length && src[i] == '\n') { i++; line++; }
            if (bracketDepth == 0 && !continuation && src.Length > 0)
                toks.Add(new Token(Tt.Newline, "", line));
        }
        while (indents.Count > 1)
        {
            indents.RemoveAt(indents.Count - 1);
            toks.Add(new Token(Tt.Dedent, "", line));
        }
        toks.Add(new Token(Tt.Eof, "", line));
        return toks;
    }

    private static string? ReadPrefix(string src, ref int i)
    {
        int start = i;
        while (i < src.Length && "rRbBuUfF".Contains(src[i])) i++;
        if (i < src.Length && (src[i] == '"' || src[i] == '\'') && i > start)
            return src[start..i].ToLowerInvariant();
        i = start;
        return null;
    }

    /// <summary>Reads a Python string literal; returns token kind (Str/FStr) and its value (f-strings keep raw inner text).</summary>
    private static (Tt Kind, string Text) ReadPyString(string src, ref int i, ref int line, string path, string prefix)
    {
        char quote = src[i];
        bool raw = prefix.Contains('r');
        bool fstring = prefix.Contains('f');
        int startLine = line;
        i++;
        bool triple = i + 1 < src.Length && src[i] == quote && src[i + 1] == quote;
        if (triple) i += 2;
        var sb = new System.Text.StringBuilder();
        int closed = 0;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n')
            {
                if (!triple) throw new ParseHardError($"{path}:{startLine}: unterminated string literal");
                line++;
                sb.Append('\n');
                i++;
                continue;
            }
            if (c == quote)
            {
                if (triple)
                {
                    if (i + 2 < src.Length + 1 && i + 2 <= src.Length - 1 && src[i + 1] == quote && src[i + 2] == quote)
                    {
                        i += 3;
                        closed = 1;
                        break;
                    }
                    sb.Append(c);
                    i++;
                    continue;
                }
                i++;
                closed = 1;
                break;
            }
            if (c == '\\' && !raw && i + 1 < src.Length)
            {
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
        if (closed == 0) throw new ParseHardError($"{path}:{startLine}: unterminated string literal");
        return fstring ? (Tt.FStr, sb.ToString()) : (Tt.Str, sb.ToString());
    }

    // ---- parser ----------------------------------------------------------

    private sealed class Parser
    {
        private readonly List<Token> _toks;
        private readonly string _path;
        private int _pos;

        public Parser(List<Token> toks, string path) { _toks = toks; _path = path; }

        private Token Cur => _toks[_pos];
        private bool IsName(string text) => Cur.Type == Tt.Name && Cur.Text == text;
        private bool IsOp(string text) => Cur.Type == Tt.Op && Cur.Text == text;
        private Token Take() => _toks[_pos++];

        public CodeModel ParseModule()
        {
            var types = new List<TypeDecl>();
            var moduleFunctions = new List<MethodDecl>();
            string moduleStem = ModuleStem(_path);

            while (Cur.Type != Tt.Eof)
            {
                switch (Cur.Type)
                {
                    case Tt.Newline or Tt.Indent or Tt.Dedent:
                        Take();
                        continue;
                    case Tt.Op when Cur.Text == "@":
                        var attrs = ParseDecorators();
                        if (IsName("async")) Take();
                        if (IsName("def")) ParseDef(attrs, moduleStem, moduleFunctions);
                        else if (IsName("class")) ParseClass(attrs, types);
                        else SkipStatement();
                        continue;
                    case Tt.Name when Cur.Text == "async" && _toks[_pos + 1].Type == Tt.Name && _toks[_pos + 1].Text == "def":
                        Take(); // async
                        ParseDef(new List<AttributeDecl>(), moduleStem, moduleFunctions);
                        continue;
                    case Tt.Name when Cur.Text == "def":
                        ParseDef(new List<AttributeDecl>(), moduleStem, moduleFunctions);
                        continue;
                    case Tt.Name when Cur.Text == "class":
                        ParseClass(new List<AttributeDecl>(), types);
                        continue;
                    default:
                        SkipStatement();
                        continue;
                }
            }
            if (moduleFunctions.Count > 0)
                types.Add(new TypeDecl(moduleStem, null, moduleFunctions));
            return new CodeModel(_path, types);
        }

        private static string ModuleStem(string relativePath)
        {
            string name = relativePath;
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (slash >= 0) name = name[(slash + 1)..];
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name[..dot];
            return name;
        }

        private List<AttributeDecl> ParseDecorators()
        {
            var attrs = new List<AttributeDecl>();
            while (IsOp("@"))
            {
                Take();
                var chain = new List<string>();
                while (Cur.Type == Tt.Name)
                {
                    chain.Add(Take().Text);
                    if (IsOp(".")) { Take(); continue; }
                    break;
                }
                var args = new List<AttributeArg>();
                if (IsOp("(")) args = ParseAttributeArgs(CollectBalanced('(', ')'));
                attrs.Add(new AttributeDecl(string.Join('.', chain), args));
                if (Cur.Type == Tt.Newline) Take();
                while (Cur.Type == Tt.Newline) Take();
            }
            return attrs;
        }

        /// <summary>Parses a def at module or class level; the resulting method is appended to <paramref name="destination"/>.</summary>
        private void ParseDef(List<AttributeDecl> attrs, string ownerType, List<MethodDecl> destination)
        {
            Take(); // 'def'
            if (Cur.Type != Tt.Name) { SkipStatement(); return; }
            int line = Cur.Line;
            string name = Take().Text;
            var parameters = new List<string>();
            if (IsOp("(")) parameters = ParseParams(CollectBalanced('(', ')'));
            // skip return annotation and ':'
            while (Cur.Type is not (Tt.Newline or Tt.Eof or Tt.Indent or Tt.Dedent)) Take();

            var invocations = Cur.Type == Tt.Newline
                ? (TakeIfIndent() ? ScanSuite() : new List<InvocationDecl>())
                : ScanSimpleBody();
            destination.Add(new MethodDecl(name, ownerType, null, line, parameters, attrs, invocations));
        }

        /// <summary>True when cur is the INDENT of a suite (taking the preceding NEWLINE if needed).</summary>
        private bool TakeIfIndent()
        {
            if (Cur.Type == Tt.Newline) Take();
            return Cur.Type == Tt.Indent;
        }

        private void ParseClass(List<AttributeDecl> attrs, List<TypeDecl> types)
        {
            Take(); // 'class'
            if (Cur.Type != Tt.Name) { SkipStatement(); return; }
            string name = Take().Text;
            var bases = new List<string>();
            if (IsOp("(")) bases = ParseClassBases(CollectBalanced('(', ')'));
            // skip ':' / annotations
            while (Cur.Type is not (Tt.Newline or Tt.Eof or Tt.Indent or Tt.Dedent)) Take();
            if (Cur.Type == Tt.Newline) Take();

            var methods = new List<MethodDecl>();
            if (Cur.Type == Tt.Indent)
            {
                Take();
                int depth = 1;
                while (Cur.Type != Tt.Eof && depth > 0)
                {
                    switch (Cur.Type)
                    {
                        case Tt.Indent: depth++; Take(); continue;
                        case Tt.Dedent: depth--; Take(); continue;
                        case Tt.Newline: Take(); continue;
                        case Tt.Op when Cur.Text == "@":
                            var mattrs = ParseDecorators();
                            if (IsName("async")) Take();
                            if (IsName("def")) ParseDef(mattrs, name, methods);
                            else if (IsName("class")) ParseClass(mattrs, types);
                            else SkipStatement();
                            continue;
                        case Tt.Name when Cur.Text == "async" && _toks[_pos + 1].Type == Tt.Name && _toks[_pos + 1].Text == "def":
                            Take(); // async
                            ParseDef(new List<AttributeDecl>(), name, methods);
                            continue;
                        case Tt.Name when Cur.Text == "def":
                            ParseDef(new List<AttributeDecl>(), name, methods);
                            continue;
                        case Tt.Name when Cur.Text == "class":
                            ParseClass(new List<AttributeDecl>(), types);
                            continue;
                        default:
                            SkipStatement();
                            continue;
                    }
                }
            }
            types.Add(new TypeDecl(name, null, methods, bases));
        }

        /// <summary>Collects the raw tokens of an indented suite and scans it for invocations (nested defs flatten into the outer method).</summary>
        private List<InvocationDecl> ScanSuite()
        {
            Take(); // INDENT
            var body = new List<Token>();
            int depth = 1;
            while (Cur.Type != Tt.Eof && depth > 0)
            {
                if (Cur.Type == Tt.Indent) depth++;
                else if (Cur.Type == Tt.Dedent) { depth--; if (depth == 0) { Take(); break; } }
                if (depth > 0) body.Add(Cur);
                Take();
            }
            return ScanCalls(body);
        }

        private List<InvocationDecl> ScanSimpleBody()
        {
            var body = new List<Token>();
            while (Cur.Type is not (Tt.Newline or Tt.Eof or Tt.Indent or Tt.Dedent))
            {
                body.Add(Cur);
                Take();
            }
            if (Cur.Type == Tt.Newline) Take();
            return ScanCalls(body);
        }

        private void SkipStatement()
        {
            while (Cur.Type is not (Tt.Newline or Tt.Eof or Tt.Indent or Tt.Dedent)) Take();
            if (Cur.Type == Tt.Newline) Take();
        }

        private List<string> ParseParams(List<Token> toks)
        {
            var names = new List<string>();
            foreach (var chunk in SplitTopLevel(toks, ","))
            {
                if (chunk.Count == 0) continue;
                if (chunk[0].Type == Tt.Op && chunk[0].Text is "*" or "**") continue;
                if (chunk[0].Type == Tt.Name) names.Add(chunk[0].Text);
            }
            return names;
        }

        private List<string> ParseClassBases(List<Token> toks)
        {
            var bases = new List<string>();
            foreach (var chunk in SplitTopLevel(toks, ","))
            {
                if (chunk.Count == 0) continue;
                var chain = new List<string>();
                int k = 0;
                while (k < chunk.Count && chunk[k].Type == Tt.Name)
                {
                    chain.Add(chunk[k].Text);
                    k++;
                    if (k < chunk.Count && chunk[k].Type == Tt.Op && chunk[k].Text == ".") { k++; continue; }
                    break;
                }
                if (chain.Count > 0) bases.Add(string.Join('.', chain));
            }
            return bases;
        }

        private List<AttributeArg> ParseAttributeArgs(List<Token> toks)
        {
            var args = new List<AttributeArg>();
            foreach (var chunk in SplitTopLevel(toks, ","))
            {
                if (chunk.Count == 0) continue;
                var (name, valueChunk) = SplitKeywordArg(chunk);
                var arg = ClassifyChunk(valueChunk);
                args.Add(new AttributeArg(arg.Kind, arg.Value, name));
            }
            return args;
        }

        /// <summary>Splits NAME=value (keyword arg) if present; returns the keyword name or null.</summary>
        private static (string? Name, List<Token> RestTokens) SplitKeywordArg(List<Token> chunk)
        {
            if (chunk.Count >= 2 && chunk[0].Type == Tt.Name && chunk[1].Type == Tt.Op && chunk[1].Text == "=")
                return (chunk[0].Text, chunk[2..]);
            return (null, chunk);
        }

        private AttributeArg ClassifyChunk(List<Token> toks)
        {
            if (toks.Count == 0) return new AttributeArg(ArgKind.Expression, "");
            // list/tuple literal of literals → List
            if (IsLiteralSequence(toks, out string? joined) && joined != null)
                return new AttributeArg(ArgKind.List, joined);
            if (toks.Count == 1)
            {
                var t = toks[0];
                return t.Type switch
                {
                    Tt.Str => new AttributeArg(ArgKind.Literal, t.Text),
                    Tt.FStr => new AttributeArg(ArgKind.Interpolated, t.Text),
                    Tt.Number => new AttributeArg(ArgKind.Literal, t.Text),
                    Tt.Name when t.Text is "True" or "False" or "None" => new AttributeArg(ArgKind.Literal, t.Text),
                    Tt.Name => new AttributeArg(ArgKind.Expression, t.Text),
                    _ => new AttributeArg(ArgKind.Expression, "")
                };
            }
            // dotted name
            if (toks.Count % 2 == 1 && toks.All(t => t.Type == Tt.Name || (t.Type == Tt.Op && t.Text == ".")))
                return new AttributeArg(ArgKind.Expression, string.Join('.', toks.Where(t => t.Type == Tt.Name).Select(t => t.Text)));
            // string concat "a" + "b" or "a" "b"
            if (IsStringConcat(toks, out string? concat) && concat != null)
                return new AttributeArg(ArgKind.Literal, concat);
            return new AttributeArg(ArgKind.Expression, "");
        }

        /// <summary>Collects the inner tokens of a balanced bracket block starting at cur (the opening bracket).</summary>
        private List<Token> CollectBalanced(char open, char close)
        {
            var inner = new List<Token>();
            Take(); // opening
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
                inner.Add(Cur);
                Take();
            }
            throw new ParseHardError($"{_path}: unbalanced '{open}'");
        }

        // ---- suite call scanning ------------------------------------------

        private List<InvocationDecl> ScanCalls(List<Token> body)
        {
            var invocations = new List<InvocationDecl>();
            int i = 0;
            while (i < body.Count)
            {
                var t = body[i];
                if (t.Type != Tt.Name) { i++; continue; }
                bool prevIsDefKeyword = i > 0 && body[i - 1].Type == Tt.Name && body[i - 1].Text is "def" or "class" or "lambda";
                if (i + 1 < body.Count && body[i + 1].Type == Tt.Op && body[i + 1].Text == "(")
                {
                    if (prevIsDefKeyword)
                    {
                        // parameter list of a nested def/class/lambda: skip it, body continues
                        int j = i + 1;
                        _ = CollectCallArgs(body, ref j);
                        i = j;
                        continue;
                    }
                    // receiver = dotted chain left of the call
                    string? receiver = null;
                    if (i > 0 && body[i - 1].Type == Tt.Op && body[i - 1].Text == ".")
                        receiver = WalkReceiverChain(body, i);
                    int j2 = i + 1;
                    var argChunks = CollectCallArgs(body, ref j2);
                    invocations.Add(new InvocationDecl(receiver, t.Text, t.Line, argChunks.Select(ClassifyArg).ToList()));
                    foreach (var c in argChunks) invocations.AddRange(ScanCalls(c));
                    i = j2;
                    continue;
                }
                i++;
            }
            return invocations;
        }

        /// <summary>Dotted receiver chain immediately left of the method name (requests.get → requests; urllib.request.urlopen → urllib.request).</summary>
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
                if (tk.Type == Tt.Name && depth == 0)
                {
                    chain.Insert(0, tk.Text);
                    if (k - 1 >= 0 && body[k - 1].Type == Tt.Op && body[k - 1].Text == ".") { k--; continue; }
                    break;
                }
                break;
            }
            return chain.Count > 0 ? string.Join('.', chain) : null;
        }

        /// <summary>Splits a call's argument token list on top-level commas (position j at the opening paren; returns j past the closing paren).</summary>
        private static List<List<Token>> CollectCallArgs(List<Token> toks, ref int j)
        {
            var args = new List<List<Token>>();
            int depth = 1;
            j++; // past '('
            var current = new List<Token>();
            while (j < toks.Count)
            {
                var tk = toks[j];
                if (tk.Type == Tt.Op && (tk.Text == "(" || tk.Text == "[" || tk.Text == "{")) depth++;
                else if (tk.Type == Tt.Op && (tk.Text == ")" || tk.Text == "]" || tk.Text == "}")) depth--;
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

        private CallArg ClassifyArg(List<Token> toks)
        {
            if (toks.Count == 0) return new CallArg(ArgKind.Expression, "");
            // keyword argument: name=value
            if (toks.Count >= 2 && toks[0].Type == Tt.Name && toks[1].Type == Tt.Op && toks[1].Text == "="
                && toks[0].Text is not ("True" or "False" or "None"))
            {
                var inner = ClassifyArg(toks[2..]);
                return new CallArg(inner.Kind, inner.Value, toks[0].Text);
            }
            // list/tuple literal of literals (argv, address tuples)
            if (IsLiteralSequence(toks, out string? joined) && joined != null)
                return new CallArg(ArgKind.List, joined);
            if (toks.Count == 1)
            {
                var t = toks[0];
                return t.Type switch
                {
                    Tt.Str => CallArg.Literal(t.Text),
                    Tt.FStr => new CallArg(ArgKind.Interpolated, t.Text),
                    Tt.Number => CallArg.Literal(t.Text),
                    Tt.Name when t.Text is "True" or "False" or "None" => new CallArg(ArgKind.Identifier, t.Text),
                    Tt.Name => new CallArg(ArgKind.Identifier, t.Text),
                    _ => new CallArg(ArgKind.Expression, "")
                };
            }
            // dotted name a.b.c
            if (toks.Count % 2 == 1 && toks.All(t => t.Type == Tt.Name || (t.Type == Tt.Op && t.Text == ".")))
                return new CallArg(ArgKind.Identifier, string.Join('.', toks.Where(t => t.Type == Tt.Name).Select(t => t.Text)));
            // string concat "a" + "b" / "a" "b"
            if (IsStringConcat(toks, out string? concat) && concat != null)
                return CallArg.Literal(concat);
            return new CallArg(ArgKind.Expression, "");
        }

        /// <summary>List/tuple literal whose elements are all literals: [a, b], (a, b), (a,).</summary>
        private static bool IsLiteralSequence(List<Token> toks, out string? joined)
        {
            joined = null;
            if (toks.Count < 3) return false;
            bool isList = toks[0].Type == Tt.Op && toks[0].Text == "[" && toks[^1].Type == Tt.Op && toks[^1].Text == "]";
            bool isTuple = toks[0].Type == Tt.Op && toks[0].Text == "(" && toks[^1].Type == Tt.Op && toks[^1].Text == ")";
            if (!isList && !isTuple) return false;
            var items = SplitTopLevel(toks[1..^1], ",");
            if (items.Count == 0) return false;
            if (!items.All(x => x.Count == 1 && (x[0].Type is Tt.Str or Tt.Number || (x[0].Type == Tt.Name && x[0].Text is "True" or "False" or "None")))) return false;
            joined = string.Join('\u0001', items.Select(x => x[0].Text));
            return true;
        }

        /// <summary>Adjacent or +-joined string literals (folding only when all parts are plain literals).</summary>
        private static bool IsStringConcat(List<Token> toks, out string? concat)
        {
            concat = null;
            var parts = new List<string>();
            int i = 0;
            while (i < toks.Count)
            {
                if (i % 2 == 0)
                {
                    if (toks[i].Type != Tt.Str) return false;
                    parts.Add(toks[i].Text);
                    i++;
                    // implicit concatenation: "a" "b" without operator
                    while (i < toks.Count && toks[i].Type == Tt.Str) { parts.Add(toks[i].Text); i++; }
                    if (i >= toks.Count) break;
                    if (toks[i].Type == Tt.Op && toks[i].Text == "+") { i++; continue; }
                    return false;
                }
                return false;
            }
            if (parts.Count < 2) return false;
            concat = string.Concat(parts);
            return true;
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
