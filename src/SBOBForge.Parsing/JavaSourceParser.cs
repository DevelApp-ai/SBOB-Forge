using System.Text;
using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// Deterministic hand-rolled Java source parser (TDS 5.2 / 7): tokenizer +
/// lightweight structural parser producing the language-neutral CodeModel.
/// Pinned as lock grammar "java17" (backend "sbobforge-java17").
/// Scope: package, imports, top-level types, methods/constructors (name,
/// line, params, annotations incl. array-initializer args), invocation
/// sites with argument kinds. Generics, lambdas and nested types are
/// tolerated but not modeled. Unterminated strings or unbalanced braces
/// are hard errors (invalid input, TDS 13).
/// </summary>
public sealed class JavaSourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-java17";
    public const string Version = "1.0.1";

    string ICodeModelParser.BackendId => BackendId;

    public CodeModel Parse(string relativePath, string sourceText)
    {
        var tokens = Tokenize(sourceText, relativePath);
        return new Parser(tokens, relativePath).ParseCompilationUnit();
    }

    // ---- tokenizer -------------------------------------------------------

    private enum Tt { Ident, Keyword, String, Char, Number, Op, Eof }

    private sealed record Token(Tt Type, string Text, int Line);

    private static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal)
    {
        "package","import","public","private","protected","static","final","abstract","class",
        "interface","enum","record","extends","implements","new","return","if","else","for",
        "while","do","switch","case","default","break","continue","try","catch","finally",
        "throw","throws","this","super","void","boolean","byte","short","int","long","float",
        "double","char","true","false","null","instanceof","synchronized","native","transient",
        "volatile","strictfp","assert","goto","const","sealed","permits","yield","var"
    };

    private static readonly HashSet<string> s_modifiers = new(StringComparer.Ordinal)
    {
        "public","private","protected","static","final","abstract","synchronized","native",
        "transient","volatile","strictfp","sealed"
    };

    private static readonly HashSet<string> s_primTypes = new(StringComparer.Ordinal)
    {
        "void","int","boolean","long","double","float","char","byte","short","var"
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
                toks.Add(new Token(Tt.String, ReadString(src, ref i, ref line, path, '"'), line));
                continue;
            }
            if (c == '\'')
            {
                toks.Add(new Token(Tt.Char, ReadString(src, ref i, ref line, path, '\''), line));
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '$')) i++;
                string word = src[s..i];
                toks.Add(new Token(s_keywords.Contains(word) ? Tt.Keyword : Tt.Ident, word, line));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '.' || src[i] == '_')) i++;
                if (i < src.Length && (src[i] == '+' || src[i] == '-') && "eE".Contains(src[i - 1])) i++;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++; // suffix
                toks.Add(new Token(Tt.Number, src[s..i], line));
                continue;
            }
            string three = i + 2 < src.Length ? src.Substring(i, 3) : "";
            string two = i + 1 < src.Length ? src.Substring(i, 2) : "";
            if (three == "->>") { toks.Add(new(Tt.Op, three, line)); i += 3; continue; }
            if (two is "->" or "::" or "<<" or ">>" or "==" or "!=" or "<=" or ">=" or "&&" or "||"
                or "+=" or "-=" or "*=" or "/=" or "%=" or "++" or "--" or "..." or "&=" or "|=" or "^=")
            { toks.Add(new(Tt.Op, two, line)); i += 2; continue; }
            toks.Add(new Token(Tt.Op, c.ToString(), line));
            i++;
        }
        toks.Add(new Token(Tt.Eof, "", line));
        return toks;
    }

    private static string ReadString(string src, ref int i, ref int line, string path, char quote)
    {
        int startLine = line;
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < src.Length && src[i] != quote)
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
        private int _pos;

        public Parser(List<Token> toks, string path) { _toks = toks; _path = path; }

        private Token Cur => _toks[_pos];
        private bool IsOp(string text) => Cur.Type == Tt.Op && Cur.Text == text;
        private bool IsKw(string text) => Cur.Type == Tt.Keyword && Cur.Text == text;
        private Token Take() => _toks[_pos++];

        public CodeModel ParseCompilationUnit()
        {
            string? ns = null;
            var imports = new List<string>();
            var types = new List<TypeDecl>();

            while (Cur.Type != Tt.Eof)
            {
                if (IsKw("package"))
                {
                    Take();
                    var sb = new StringBuilder();
                    while (Cur.Type != Tt.Eof && !IsOp(";"))
                    {
                        if (Cur.Type == Tt.Ident) { if (sb.Length > 0) sb.Append('.'); sb.Append(Cur.Text); }
                        Take();
                    }
                    ns = sb.ToString();
                    ExpectSemi();
                    continue;
                }
                if (IsKw("import"))
                {
                    Take();
                    var sb = new StringBuilder();
                    if (IsKw("static")) { sb.Append("static "); Take(); }
                    while (Cur.Type == Tt.Ident)
                    {
                        if (sb.Length > 0 && sb[^1] != ' ') sb.Append('.');
                        sb.Append(Take().Text);
                        if (IsOp(".")) { Take(); continue; }
                        break;
                    }
                    if (IsOp("*")) { sb.Append('*'); Take(); }
                    imports.Add(sb.ToString());
                    ExpectSemi();
                    continue;
                }
                if (IsOp(";")) { Take(); continue; }
                ParseTypeDecl(types, ns, null);
            }
            return new CodeModel(_path, types, imports);
        }

        private void ExpectSemi()
        {
            while (Cur.Type != Tt.Eof && !IsOp(";")) Take();
            if (IsOp(";")) Take();
        }

        /// <summary>Consumes a balanced block starting at cur (must be the opening bracket).</summary>
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

        /// <summary>Collects the inner tokens of a balanced block starting at cur (opening bracket); outer brackets excluded.</summary>
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

        private void ParseTypeDecl(List<TypeDecl> types, string? ns, string? declaring)
        {
            while (IsOp("@")) ParseAnnotation();

            while (Cur.Type == Tt.Keyword && s_modifiers.Contains(Cur.Text)) Take();

            bool isAnnotationDecl = IsOp("@") && _toks[_pos + 1].Type == Tt.Ident && _toks[_pos + 1].Text == "interface";
            if (IsKw("class") || IsKw("interface") || IsKw("enum") || IsKw("record") || isAnnotationDecl)
            {
                if (isAnnotationDecl) Take(); // '@'
                Take(); // class keyword
                if (Cur.Type != Tt.Ident) { SkipToTypeEnd(); return; }
                string name = Take().Text;
                SkipGenericArgs();

                var bases = new List<string>();
                while (!IsOp("{") && Cur.Type != Tt.Eof)
                {
                    if (IsKw("extends") || IsKw("implements") || IsKw("permits")) { Take(); continue; }
                    if (Cur.Type == Tt.Ident)
                    {
                        var chain = ReadDottedType();
                        if (chain.Length > 0) bases.Add(chain);
                        continue;
                    }
                    Take();
                }
                if (!IsOp("{")) { SkipToTypeEnd(); return; }

                var fullName = declaring == null ? name : declaring + "." + name;
                var methods = ParseTypeBody(fullName, ns);
                types.Add(new TypeDecl(fullName, ns, methods, bases));
                return;
            }
            // not a type — skip a token to guarantee progress
            if (Cur.Type != Tt.Eof) Take();
        }

        private string ReadDottedType()
        {
            var sb = new StringBuilder();
            while (Cur.Type == Tt.Ident)
            {
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Take().Text);
                SkipGenericArgs();
                if (IsOp(".")) { Take(); continue; }
                break;
            }
            return sb.ToString();
        }

        private void SkipGenericArgs()
        {
            if (!IsOp("<")) return;
            int depth = 0;
            while (Cur.Type != Tt.Eof)
            {
                var t = Take();
                if (t.Type == Tt.Op && t.Text == "<") depth++;
                else if (t.Type == Tt.Op && t.Text == ">") { depth--; if (depth == 0) return; }
                else if (t.Type == Tt.Op && t.Text == ">>") { depth -= 2; if (depth <= 0) return; }
                else if (t.Type == Tt.Op && t.Text == ";") return; // not generics after all
            }
        }

        private void SkipToTypeEnd()
        {
            while (Cur.Type != Tt.Eof && !IsOp("{") && !IsOp(";")) Take();
            if (IsOp("{")) SkipBalanced('{', '}');
            else if (IsOp(";")) Take();
        }

        private List<MethodDecl> ParseTypeBody(string typeName, string? ns)
        {
            var methods = new List<MethodDecl>();
            int depth = 1;
            Take(); // '{' of the type body
            while (Cur.Type != Tt.Eof && depth > 0)
            {
                if (IsOp("}")) { Take(); depth--; continue; }
                if (IsOp("{")) { SkipBalanced('{', '}'); continue; } // initializer block
                if (IsOp(";")) { Take(); continue; }

                var attrs = new List<AttributeDecl>();
                while (IsOp("@")) attrs.Add(ParseAnnotation());

                while (Cur.Type == Tt.Keyword && s_modifiers.Contains(Cur.Text)) Take();

                if (IsKw("class") || IsKw("interface") || IsKw("enum") || IsKw("record"))
                {
                    ParseTypeDecl(new List<TypeDecl>(), ns, typeName);
                    continue;
                }

                // constructor: <TypeName> ( ... )
                if (Cur.Type == Tt.Ident && Cur.Text == typeName
                    && _toks[_pos + 1].Type == Tt.Op && _toks[_pos + 1].Text == "(")
                {
                    int line = Take().Line;
                    var (parameters, _) = ParseParamList();
                    SkipThrows();
                    if (IsOp("{"))
                    {
                        var body = CollectBalanced('{', '}');
                        methods.Add(new MethodDecl(typeName, typeName, ns, line, parameters, attrs, ScanInvocations(body)));
                    }
                    else ExpectSemi();
                    continue;
                }

                // return type: primitive/void keyword or dotted type
                string returnType;
                if (Cur.Type == Tt.Keyword && s_primTypes.Contains(Cur.Text))
                {
                    returnType = Take().Text;
                }
                else if (Cur.Type == Tt.Ident)
                {
                    returnType = ReadDottedType();
                }
                else
                {
                    if (Cur.Type != Tt.Eof) Take();
                    continue;
                }
                SkipGenericArgs();
                while (IsOp("[")) { Take(); if (IsOp("]")) Take(); }

                if (Cur.Type != Tt.Ident)
                {
                    // field of dotted type with initializer, or unknown construct
                    while (Cur.Type != Tt.Eof && !IsOp(";") && !IsOp("{") && !IsOp("}"))
                    {
                        if (IsOp("(")) { SkipBalanced('(', ')'); continue; }
                        if (IsOp("{")) { SkipBalanced('{', '}'); continue; }
                        Take();
                    }
                    if (IsOp(";")) Take();
                    continue;
                }

                int mline = Cur.Line;
                string mname = Take().Text;
                SkipGenericArgs();

                if (!IsOp("("))
                {
                    // field
                    while (Cur.Type != Tt.Eof && !IsOp(";") && !IsOp("}"))
                    {
                        if (IsOp("(")) { SkipBalanced('(', ')'); continue; }
                        if (IsOp("{")) { SkipBalanced('{', '}'); continue; }
                        Take();
                    }
                    if (IsOp(";")) Take();
                    continue;
                }

                var (parameters2, _) = ParseParamList();
                SkipThrows();

                if (IsOp("{"))
                {
                    var body = CollectBalanced('{', '}');
                    methods.Add(new MethodDecl(mname, typeName, ns, mline, parameters2, attrs, ScanInvocations(body)));
                }
                else if (IsKw("default")) // interface method with default value
                {
                    while (Cur.Type != Tt.Eof && !IsOp(";")) Take();
                    if (IsOp(";")) Take();
                    methods.Add(new MethodDecl(mname, typeName, ns, mline, parameters2, attrs, new List<InvocationDecl>()));
                }
                else ExpectSemi();
            }
            if (depth != 0) throw new ParseHardError($"{_path}: unbalanced '{{'");
            return methods;
        }

        private void SkipThrows()
        {
            if (!IsKw("throws")) return;
            Take();
            while (Cur.Type == Tt.Ident) { ReadDottedType(); if (IsOp(",")) Take(); else break; }
        }

        private (List<string> Parameters, bool HasArgs) ParseParamList()
        {
            var names = new List<string>();
            bool hasArgs = false;
            Take(); // (
            while (Cur.Type != Tt.Eof && !IsOp(")"))
            {
                if (IsOp(",")) { Take(); continue; }
                while (IsKw("final")) Take();
                while (IsOp("@")) ParseAnnotation();
                if (Cur.Type == Tt.Ident)
                {
                    ReadDottedType(); // parameter type
                    if (IsOp("...")) Take();
                    while (IsOp("[")) { Take(); if (IsOp("]")) Take(); }
                    if (Cur.Type == Tt.Ident)
                    {
                        names.Add(Take().Text);
                        hasArgs = true;
                    }
                }
                else Take();
            }
            if (IsOp(")")) Take();
            return (names, hasArgs);
        }

        private AttributeDecl ParseAnnotation()
        {
            Take(); // @
            string name = ReadDottedType();
            var args = new List<AttributeArg>();
            if (IsOp("(")) args = ParseAttributeArgs(CollectBalanced('(', ')'));
            return new AttributeDecl(name, args);
        }

        private List<AttributeArg> ParseAttributeArgs(List<Token> toks)
        {
            var args = new List<AttributeArg>();
            foreach (var chunk in SplitTopLevel(toks, ","))
            {
                if (chunk.Count == 0) continue;
                if (chunk.Count >= 3 && chunk[0].Type == Tt.Ident && chunk[1].Type == Tt.Op && chunk[1].Text == "=")
                    args.Add(ClassifyAnnotationValue(chunk[2..], chunk[0].Text));
                else
                    args.Add(ClassifyAnnotationValue(chunk, null));
            }
            return args;
        }

        private AttributeArg ClassifyAnnotationValue(List<Token> toks, string? name)
        {
            if (toks.Count >= 2 && toks[0].Type == Tt.Op && toks[0].Text == "{" && toks[^1].Type == Tt.Op && toks[^1].Text == "}")
            {
                var items = SplitTopLevel(toks[1..^1], ",");
                if (items.Count > 0 && items.All(IsLiteralChunk))
                    return new AttributeArg(ArgKind.List, string.Join('\u0001', items.Select(ChunkValue)), name);
                return new AttributeArg(ArgKind.Expression, "", name);
            }
            if (IsLiteralChunk(toks))
                return new AttributeArg(ArgKind.Literal, ChunkValue(toks), name);
            return new AttributeArg(ArgKind.Expression, "", name);
        }

        /// <summary>true/false count as literals in annotations (TestNG enabled=false).</summary>
        private static bool IsLiteralChunk(List<Token> chunk) =>
            chunk.Count == 1 && chunk[0].Type is Tt.String or Tt.Char or Tt.Number
            || (chunk.Count == 1 && chunk[0].Type == Tt.Keyword && chunk[0].Text is "true" or "false");

        private static string ChunkValue(List<Token> chunk) => chunk[0].Text;

        // ---- body scanning ---------------------------------------------------

        private List<InvocationDecl> ScanInvocations(List<Token> body)
        {
            var invocations = new List<InvocationDecl>();
            int i = 0;
            while (i < body.Count)
            {
                var t = body[i];
                if (t.Type == Tt.Keyword && t.Text == "new")
                {
                    int j = i + 1;
                    var segments = new List<string>();
                    while (j < body.Count && body[j].Type == Tt.Ident)
                    {
                        segments.Add(body[j].Text);
                        j++;
                        if (j < body.Count && body[j].Type == Tt.Op && body[j].Text == ".") { j++; continue; }
                        break;
                    }
                    j = SkipGenericTokens(body, j);
                    if (j < body.Count && body[j].Type == Tt.Op && body[j].Text == "(" && segments.Count > 0)
                    {
                        var argTokens = CollectCallArgs(body, ref j);
                        invocations.Add(new InvocationDecl(null, segments[^1], t.Line, argTokens.Select(ClassifyArg).ToList()));
                        foreach (var a in argTokens) invocations.AddRange(ScanInvocations(a));
                        i = j;
                        continue;
                    }
                    i = j > i + 1 ? j : i + 1;
                    continue;
                }
                if (t.Type == Tt.Ident && i + 1 < body.Count && body[i + 1].Type == Tt.Op && body[i + 1].Text == "(")
                {
                    string? receiver = null;
                    if (i > 0 && body[i - 1].Type == Tt.Op && body[i - 1].Text == ".")
                        receiver = ReceiverDisplay(WalkReceiverChain(body, i));
                    int j = i + 1;
                    var argTokens = CollectCallArgs(body, ref j);
                    if (t.Text is not ("this" or "super"))
                    {
                        invocations.Add(new InvocationDecl(receiver, t.Text, t.Line, argTokens.Select(ClassifyArg).ToList()));
                        foreach (var a in argTokens) invocations.AddRange(ScanInvocations(a));
                    }
                    i = j;
                    continue;
                }
                i++;
            }
            return invocations;
        }

        /// <summary>Collects the receiver-chain identifiers immediately left of index i (the method name), skipping balanced call parens in the chain.</summary>
        private static List<string> WalkReceiverChain(List<Token> body, int i)
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
            return chain;
        }

        /// <summary>Receiver display: pure identifier chain → last segment when it looks method-like (Runtime.getRuntime → getRuntime), else the dotted chain (System.out stays out).</summary>
        private static string? ReceiverDisplay(List<string> chain)
        {
            if (chain.Count == 0) return null;
            if (chain.Count == 1) return chain[0];
            string last = chain[^1];
            if (!char.IsUpper(last[0])) return last; // method-like chain segment
            return string.Join('.', chain);
        }

        private static int SkipGenericTokens(List<Token> toks, int j)
        {
            if (j >= toks.Count || toks[j].Type != Tt.Op || toks[j].Text != "<") return j;
            int depth = 0;
            while (j < toks.Count)
            {
                if (toks[j].Type == Tt.Op && toks[j].Text == "<") { depth++; j++; continue; }
                if (toks[j].Type == Tt.Op && toks[j].Text == ">") { depth--; j++; if (depth == 0) return j; continue; }
                if (depth == 0) return j;
                j++;
            }
            return j;
        }

        /// <summary>Collects the argument token lists of a call whose '(' is at index j. On return j points past the closing paren.</summary>
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

        private CallArg ClassifyArg(List<Token> toks)
        {
            if (toks.Count == 0) return new CallArg(ArgKind.Expression, "");
            // cast: (Type) expr
            if (toks[0].Type == Tt.Op && toks[0].Text == "(")
            {
                int depth = 0, close = -1;
                for (int i = 0; i < toks.Count; i++)
                {
                    if (toks[i].Type == Tt.Op && toks[i].Text == "(") depth++;
                    else if (toks[i].Type == Tt.Op && toks[i].Text == ")") { depth--; if (depth == 0) { close = i; break; } }
                }
                if (close >= 0 && close < toks.Count - 1)
                    return ClassifyArg(toks[(close + 1)..]);
            }
            if (toks.Count == 1)
            {
                var t = toks[0];
                return t.Type switch
                {
                    Tt.String or Tt.Char or Tt.Number => CallArg.Literal(t.Text),
                    Tt.Keyword when t.Text is "true" or "false" or "null" => new CallArg(ArgKind.Identifier, t.Text),
                    Tt.Ident => new CallArg(ArgKind.Identifier, t.Text),
                    _ => new CallArg(ArgKind.Expression, "")
                };
            }
            // dotted identifier chain a.b.c
            if (toks.Count % 2 == 1 && toks.All(t => t.Type == Tt.Ident || (t.Type == Tt.Op && t.Text == ".")))
                return new CallArg(ArgKind.Identifier, string.Join('.', toks.Where(t => t.Type == Tt.Ident).Select(t => t.Text)));
            // Path.of(lit) / Paths.get(lit): constant-fold (TDS 7.3 tier L)
            if (IsPathCall(toks, out List<Token>? inner) && inner!.Count == 1 && inner[0].Type == Tt.String)
                return CallArg.Literal(inner[0].Text);
            // Path.of(x)/Paths.get(x) with single non-literal arg: propagate the inner kind
            if (IsPathCall(toks, out inner) && inner!.Count == 1)
                return ClassifyArg(inner);
            // string concat of literals: "a" + "b"
            if (IsStringConcat(toks, out string? concat) && concat != null)
                return CallArg.Literal(concat);
            // literal array initializer / List.of(...) / new T[]{...}
            if (IsLiteralList(toks, out string? joined) && joined != null)
                return new CallArg(ArgKind.List, joined);
            // partially-literal array/List.of: argv0 anchor (TDS 5.2 argv0-anchoring)
            if (IsAnchoredList(toks, out string? first) && first != null)
                return new CallArg(ArgKind.PartialList, first);
            return new CallArg(ArgKind.Expression, "");
        }

        private static bool IsPathCall(List<Token> toks, out List<Token>? inner)
        {
            inner = null;
            if (toks.Count < 5) return false;
            if (toks[0].Type != Tt.Ident || toks[0].Text is not ("Path" or "Paths")) return false;
            if (toks[1].Type != Tt.Op || toks[1].Text != ".") return false;
            if (toks[2].Type != Tt.Ident || toks[2].Text is not ("of" or "get")) return false;
            if (toks[3].Type != Tt.Op || toks[3].Text != "(") return false;
            if (toks[^1].Type != Tt.Op || toks[^1].Text != ")") return false;
            var inside = toks[4..^1];
            // inner must be flat (no nested call)
            if (inside.Any(t => t.Type == Tt.Op && t.Text == "(")) return false;
            inner = inside;
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
                    if (toks[i].Type != Tt.String) return false;
                    parts.Add(toks[i].Text);
                }
                else if (toks[i].Type != Tt.Op || toks[i].Text != "+") return false;
            }
            concat = string.Concat(parts);
            return true;
        }

        private static bool IsLiteralList(List<Token> toks, out string? joined)
        {
            joined = null;
            if (!TryListItems(toks, out var items)) return false;
            if (!items.All(x => x.Count == 1 && x[0].Type is Tt.String or Tt.Char or Tt.Number)) return false;
            joined = string.Join('\u0001', items.Select(x => x[0].Text));
            return true;
        }

        /// <summary>Array initializer / List.of whose first element is a literal but the
        /// sequence is not all-literal (List wins for fully literal sequences). Yields the
        /// anchored argv0 (TDS 5.2 argv0-anchoring).</summary>
        private static bool IsAnchoredList(List<Token> toks, out string? first)
        {
            first = null;
            if (!TryListItems(toks, out var items) || items.Count == 0) return false;
            if (items[0].Count != 1 || items[0][0].Type is not (Tt.String or Tt.Char or Tt.Number)) return false;
            first = items[0][0].Text;
            return true;
        }

        private static bool TryListItems(List<Token> toks, out List<List<Token>> items)
        {
            items = new List<List<Token>>();
            if (toks.Count >= 2 && toks[0].Type == Tt.Op && toks[0].Text == "{" && toks[^1].Type == Tt.Op && toks[^1].Text == "}")
                items = SplitTopLevel(toks[1..^1], ",");
            else if (toks.Count >= 5 && toks[0].Type == Tt.Ident && toks[0].Text == "List"
                     && toks[1].Type == Tt.Op && toks[1].Text == "." && toks[2].Type == Tt.Ident && toks[2].Text == "of"
                     && toks[3].Type == Tt.Op && toks[3].Text == "(" && toks[^1].Type == Tt.Op && toks[^1].Text == ")")
                items = SplitTopLevel(toks[4..^1], ",");
            else if (toks.Count >= 5 && toks[0].Type == Tt.Keyword && toks[0].Text == "new" && toks[^1].Type == Tt.Op && toks[^1].Text == "}")
            {
                int openBrace = toks.FindIndex(t => t.Type == Tt.Op && t.Text == "{");
                if (openBrace > 1 && toks[openBrace - 1].Type == Tt.Op && toks[openBrace - 1].Text == "]")
                    items = SplitTopLevel(toks[(openBrace + 1)..^1], ",");
            }
            return items.Count > 0;
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
