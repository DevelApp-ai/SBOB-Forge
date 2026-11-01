using System.Text;
using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// Deterministic structural C parser (TDS 5.2, M5): file-scope function
/// definitions with body invocation scanning, macro-block test entries
/// (TDS 2.2 framework matrix row C), `O_*` flag masks and `fopen` mode
/// strings, compound-literal argv lists, and socket setup hoisting — the
/// port/IP literals assigned to `sin_port`/`sin_addr.s_addr` via
/// `htons`/`inet_addr` before a `connect`/`listen` call are attached to that
/// call as synthetic named arguments (documented M5 modeling decision).
/// Not a full C front end: the preprocessor is line-granular (directives
/// are skipped, `#include` recorded) and macro-expanded code is analyzed
/// as written. Pinned as lock grammar "c17" (backend "sbobforge-c17").
/// Unterminated strings/comments or unbalanced delimiters are hard errors
/// (TDS 13).
/// </summary>
public sealed class C17SourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-c17";
    public const string Version = "1.0.0";

    string ICodeModelParser.BackendId => BackendId;

    public CodeModel Parse(string relativePath, string sourceText) =>
        new CxxParserCore(isCpp: false).ParseFile(relativePath, sourceText);
}

/// <summary>
/// Deterministic structural C++ parser (TDS 5.2, M5) sharing the C core plus
/// a C++20-subset structure: namespaces and namespace aliases, classes with
/// methods and constructors, qualified (`a::b`) call chains with receiver
/// normalization, tracked variable declarations (`std::ifstream f("path")`
/// → type-normalized member calls), raw string literals, and
/// constructor-argument lowering seams (`std::system`, `std::filesystem`,
/// fstream modes, boost::asio resolver). Pinned as lock grammar "cpp20"
/// (backend "sbobforge-cpp20").
/// </summary>
public sealed class Cpp20SourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-cpp20";
    public const string Version = "1.0.0";

    string ICodeModelParser.BackendId => BackendId;

    public CodeModel Parse(string relativePath, string sourceText) =>
        new CxxParserCore(isCpp: true).ParseFile(relativePath, sourceText);
}

/// <summary>Shared deterministic C/C++ scanner (TDS 5.2).</summary>
internal sealed class CxxParserCore
{
    private readonly bool _isCpp;
    private string _path = "";
    private List<Token> _toks = new();
    private int _pos;

    private readonly Dictionary<string, string> _nsAliases = new(StringComparer.Ordinal);
    private string? _ns;

    // per-body hoisting state (sin_port = htons(N) / sin_addr.s_addr = inet_addr("ip"))
    private CallArg? _hoistedPort;
    private CallArg? _hoistedIp;

    internal CxxParserCore(bool isCpp) { _isCpp = isCpp; }

    internal CodeModel ParseFile(string path, string src)
    {
        var imports = new List<string>();
        var types = new List<TypeDecl>();
        var globalMethods = new List<MethodDecl>();
        var entryMethods = new List<MethodDecl>();
        _path = path;
        _toks = Tokenize(path, src);
        _pos = 0;
        ParseScope(imports, globalMethods, entryMethods, types);
        if (globalMethods.Count > 0)
            types.Insert(0, new TypeDecl("<global>", null, globalMethods));
        if (entryMethods.Count > 0)
            types.Add(new TypeDecl("<cxx-tests>", null, entryMethods));
        return new CodeModel(path, types, imports);
    }

    // ---- tokenizer --------------------------------------------------------

    internal enum Tt { Ident, Number, String, Char, Op, PreProc, Eof }

    internal sealed record Token(Tt Type, string Text, int Line);

    private static List<Token> Tokenize(string path, string src)
    {
        var toks = new List<Token>();
        int i = 0, line = 1;
        bool lineStart = true;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; lineStart = true; continue; }
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
            if (c == '#' && lineStart)
            {
                int start = ++i;
                bool cont = false;
                while (i < src.Length)
                {
                    if (src[i] == '\n')
                    {
                        if (cont && i > 0 && src[i - 1] == '\\') { cont = false; line++; i++; continue; }
                        break;
                    }
                    cont = src[i] == '\\';
                    i++;
                }
                toks.Add(new Token(Tt.PreProc, src[start..i].Trim(), line));
                continue;
            }
            // string/char literal prefixes: L"x", u"x", U"x", u8"x", L'c'
            if (c is 'L' or 'u' or 'U' && i + 1 < src.Length && (src[i + 1] == '"' || src[i + 1] == '\''))
            {
                bool isStr = src[i + 1] == '"';
                i++;
                toks.Add(isStr
                    ? new Token(Tt.String, ReadQuoted(src, ref i, path, line), line)
                    : new Token(Tt.Char, ReadChar(src, ref i, path, line), line));
                continue;
            }
            if (c == 'u' && i + 2 < src.Length && src[i + 1] == '8'
                && (src[i + 2] == '"' || src[i + 2] == '\''))
            {
                bool isStr = src[i + 2] == '"';
                i += 2;
                toks.Add(isStr
                    ? new Token(Tt.String, ReadQuoted(src, ref i, path, line), line)
                    : new Token(Tt.Char, ReadChar(src, ref i, path, line), line));
                continue;
            }
            if (c == 'R' && i + 1 < src.Length && src[i + 1] == '"')
            {
                int open = src.IndexOf('(', i + 2);
                if (open < 0) throw new ParseHardError($"{path}:{line}: unterminated raw string literal");
                string delim = src[(i + 2)..open];
                string closePat = ")" + delim + "\"";
                int end = src.IndexOf(closePat, open + 1, StringComparison.Ordinal);
                if (end < 0) throw new ParseHardError($"{path}:{line}: unterminated raw string literal");
                line += src[i..(end + closePat.Length)].Count(ch => ch == '\n');
                toks.Add(new Token(Tt.String, src[(open + 1)..end], line));
                i = end + closePat.Length;
                continue;
            }
            if (c == '"')
            {
                toks.Add(new Token(Tt.String, ReadQuoted(src, ref i, path, line), line));
                continue;
            }
            if (c == '\'')
            {
                toks.Add(new Token(Tt.Char, ReadChar(src, ref i, path, line), line));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                toks.Add(new Token(Tt.Ident, src[s..i], line));
                lineStart = false;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '.' || src[i] == '\'')) i++;
                toks.Add(new Token(Tt.Number, src[s..i], line));
                lineStart = false;
                continue;
            }
            string three = i + 2 < src.Length ? src.Substring(i, 3) : "";
            if (three is "<<=" or ">>=" or "->*" or "<=>" or "...") { toks.Add(new(Tt.Op, three, line)); i += 3; continue; }
            string two = i + 1 < src.Length ? src.Substring(i, 2) : "";
            if (two is "::" or "->" or "++" or "--" or "<<" or ">>" or "<=" or ">=" or "==" or "!=" or "&&" or "||"
                or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=")
            { toks.Add(new(Tt.Op, two, line)); i += 2; continue; }
            toks.Add(new Token(Tt.Op, c.ToString(), line));
            i++;
            lineStart = false;
        }
        toks.Add(new Token(Tt.Eof, "", line));
        return toks;
    }

    private static string ReadQuoted(string src, ref int i, string path, int line)
    {
        i++; // opening quote
        var sb = new StringBuilder();
        while (i < src.Length && src[i] != '"')
        {
            if (src[i] == '\n') throw new ParseHardError($"{path}:{line}: unterminated string literal");
            if (src[i] == '\\' && i + 1 < src.Length)
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
            sb.Append(src[i]); i++;
        }
        if (i >= src.Length) throw new ParseHardError($"{path}:{line}: unterminated string literal");
        i++; // closing quote
        return sb.ToString();
    }

    private static string ReadChar(string src, ref int i, string path, int line)
    {
        i++; // opening quote
        var sb = new StringBuilder();
        while (i < src.Length && src[i] != '\'')
        {
            if (src[i] == '\n') throw new ParseHardError($"{path}:{line}: unterminated character literal");
            if (src[i] == '\\' && i + 1 < src.Length) { sb.Append(src[i + 1]); i += 2; continue; }
            sb.Append(src[i]); i++;
        }
        if (i >= src.Length) throw new ParseHardError($"{path}:{line}: unterminated character literal");
        i++;
        return sb.ToString();
    }

    // ---- parser -----------------------------------------------------------

    private Token Cur => _toks[_pos];
    private bool IsOp(string text) => Cur.Type == Tt.Op && Cur.Text == text;
    private bool IsKw(string text) => Cur.Type == Tt.Ident && Cur.Text == text;
    private Token Take() => _toks[_pos++];
    private int Line => Cur.Type == Tt.Eof ? _toks[^1].Line : Cur.Line;

    private static readonly HashSet<string> s_statementKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "while", "for", "do", "switch", "case", "default", "return",
        "break", "continue", "goto", "typedef", "sizeof", "static", "const",
        "extern", "inline", "volatile", "register", "restrict", "auto",
        "struct", "union", "enum", "unsigned", "signed", "void", "char",
        "short", "int", "long", "float", "double", "bool", "_Bool", "size_t",
        "ssize_t", "FILE", "NULL", "true", "false", "new", "delete", "template",
        "using", "namespace", "operator", "public", "private", "protected",
        "virtual", "override", "final", "typename", "constexpr", "noexcept",
        "try", "catch", "throw", "decltype", "explicit", "friend", "mutable",
    };

    private static readonly HashSet<string> s_testMacros = new(StringComparer.Ordinal)
    {
        "TEST", "TEST_F", "TEST_P", "TYPED_TEST", "TYPED_TEST_P",
        "TEST_SUITE", "TEST_CASE", "TEST_CASE_METHOD", "CATCH_TEST_CASE",
        "METHOD_AS_TEST_CASE", "TEMPLATE_TEST_CASE",
    };

    /// <summary>File/namespace scope: functions, classes, test-macro blocks (TDS 5.2).</summary>
    private void ParseScope(List<string> imports, List<MethodDecl> globalMethods,
        List<MethodDecl> entryMethods, List<TypeDecl> types)
    {
        while (Cur.Type != Tt.Eof)
        {
            if (Cur.Type == Tt.PreProc)
            {
                var text = Cur.Text;
                if (text.StartsWith("include", StringComparison.Ordinal))
                {
                    int q = text.IndexOf('"');
                    if (q >= 0)
                    {
                        int e = text.IndexOf('"', q + 1);
                        if (e > q) imports.Add(text[(q + 1)..e]);
                    }
                    else
                    {
                        int l = text.IndexOf('<');
                        int r = text.IndexOf('>');
                        if (l >= 0 && r > l) imports.Add(text[(l + 1)..r]);
                    }
                }
                Take();
                continue;
            }

            if (_isCpp && IsKw("namespace"))
            {
                Take();
                if (Cur.Type == Tt.Ident)
                {
                    // qualified name: app::storage (C++17 nested namespaces)
                    var name = Take().Text;
                    while (IsOp("::") && _pos + 1 < _toks.Count && _toks[_pos + 1].Type == Tt.Ident)
                    {
                        Take();
                        name += "::" + Take().Text;
                    }
                    if (IsOp("="))
                    {
                        // namespace alias: fs = std::filesystem;
                        Take();
                        var target = ReadQualified();
                        if (target is not null) _nsAliases[name] = target;
                        if (IsOp(";")) Take();
                        continue;
                    }
                    if (IsOp("{"))
                    {
                        Take();
                        var saved = _ns;
                        _ns = _ns is null ? name : _ns + "::" + name;
                        ParseScope(imports, globalMethods, entryMethods, types);
                        _ns = saved;
                        if (IsOp("}")) Take();
                        continue;
                    }
                }
                continue;
            }
            if (_isCpp && IsKw("using"))
            {
                Take();
                if (IsKw("namespace"))
                {
                    Take();
                    ReadQualified(); // using namespace x — no alias mapping
                }
                else if (Cur.Type == Tt.Ident)
                {
                    var alias = Take().Text;
                    if (IsOp("="))
                    {
                        Take();
                        var target = ReadQualified();
                        if (target is not null) _nsAliases[alias] = target;
                    }
                }
                while (Cur.Type != Tt.Eof && !IsOp(";")) Take();
                if (IsOp(";")) Take();
                continue;
            }
            if (_isCpp && (IsKw("class") || IsKw("struct") || IsKw("union")))
            {
                var type = TryParseClass();
                if (type is not null) types.Add(type);
                continue;
            }
            if (IsKw("enum") || IsKw("typedef"))
            {
                SkipToSemicolonOrBrace();
                if (IsOp(";")) Take();
                continue;
            }
            if (IsKw("template") || IsKw("extern"))
            {
                Take();
                if (IsOp("<")) SkipBalanced('<', '>');
                if (Cur.Type == Tt.String) Take(); // extern "C"
                if (IsOp("{"))
                {
                    Take();
                    ParseScope(imports, globalMethods, entryMethods, types);
                    if (IsOp("}")) Take();
                }
                continue;
            }

            // IDENT ( … — bare test-macro block or (rare in C) a definition with no return type
            if (Cur.Type == Tt.Ident && s_testMacros.Contains(Cur.Text) && PeekIsOpenParen(1))
            {
                var entry = ParseTestMacroBlock(_pos);
                if (entry is not null) entryMethods.Add(entry);
                continue;
            }

            // [type …] name ( … ) — function definition or prototype
            var probe = _pos;
            if (ScanToFunctionName(out var namePos))
            {
                if (s_testMacros.Contains(_toks[namePos].Text))
                {
                    var entry = ParseTestMacroBlock(namePos);
                    if (entry is not null) entryMethods.Add(entry);
                    continue;
                }
                var fn = TryParseFunctionDefinition(namePos, scopeClass: null);
                if (fn is not null) globalMethods.Add(fn);
                else _pos = probe + 1;
                continue;
            }
            if (Cur.Type != Tt.Eof) Take(); // tolerate stray tokens
        }
    }

    private bool PeekIsOpenParen(int offset) =>
        _pos + offset < _toks.Count && _toks[_pos + offset] is { Type: Tt.Op, Text: "(" };

    /// <summary>From a declaration start, find `name (` at scope level (skipping types, `*`, `::`).</summary>
    private bool ScanToFunctionName(out int namePos)
    {
        namePos = -1;
        int i = _pos;
        int seenIdents = 0;
        while (i < _toks.Count)
        {
            var t = _toks[i];
            if (t.Type == Tt.Eof || t.Type == Tt.PreProc) return false;
            if (t.Type == Tt.Op && (t.Text == "(" || t.Text == "{" || t.Text == ";" || t.Text == "," || t.Text == "}"))
                return false;
            if (t.Type == Tt.Ident)
            {
                seenIdents++;
                if (i + 1 < _toks.Count && _toks[i + 1] is { Type: Tt.Op, Text: "(" })
                {
                    if (seenIdents < 2) return false; // call expression, not a definition
                    namePos = i;
                    return true;
                }
            }
            i++;
        }
        return false;
    }

    /// <summary>Parse a test-macro block `MACRO(args) { body }` (TDS 2.2 framework matrix).</summary>
    private MethodDecl? ParseTestMacroBlock(int namePos)
    {
        var macro = _toks[namePos].Text;
        var line = _toks[namePos].Line;
        _pos = namePos + 1;
        if (!IsOp("(")) return null;
        Take();
        var attrArgs = new List<AttributeArg> { new(ArgKind.Literal, macro, "macro") };
        int argIndex = 0;
        while (Cur.Type != Tt.Eof && !IsOp(")"))
        {
            var (kind, value) = ClassifyArgToken();
            attrArgs.Add(new AttributeArg(kind, value, "arg" + argIndex));
            argIndex++;
            if (IsOp(",")) Take();
        }
        if (Cur.Type == Tt.Eof) throw new ParseHardError($"{_path}:{line}: unterminated {macro} macro argument list");
        Take(); // ')'
        if (!IsOp("{"))
        {
            // no body (registration-only macro) — nothing to model
            SkipToSemicolonOrBrace();
            if (IsOp(";")) Take();
            return null;
        }
        Take();
        var invocations = ParseBody();
        if (!IsOp("}")) throw new ParseHardError($"{_path}:{line}: unterminated {macro} body block");
        Take();
        // Method name must be unique per macro instance: the pipeline matches test entries
        // to method bodies by name (EntryMethod), so a shared "TEST#2" would conflate every
        // test body (and leak disabled-body events through the enabled entries).
        return new MethodDecl(
            macro + "#" + argIndex + "@" + line, "<cxx-tests>", _ns, line,
            new[] { "<macro>" },
            new[] { new AttributeDecl("cxx.entry", attrArgs) },
            invocations);
    }

    /// <summary>Parse `[type …] name(params) { body }` positioned at the name token. Returns null for prototypes.</summary>
    private MethodDecl? TryParseFunctionDefinition(int namePos, string? scopeClass)
    {
        var name = _toks[namePos].Text;
        var line = _toks[namePos].Line;
        _pos = namePos + 1;
        if (!IsOp("(")) return null;
        Take();
        var parameters = new List<string>();
        while (Cur.Type != Tt.Eof && !IsOp(")"))
        {
            var start = _pos;
            ScanArgSpan();
            var text = string.Join(' ', Enumerable.Range(start, _pos - start).Select(k => _toks[k].Text));
            if (text.Length > 0) parameters.Add(text);
            if (IsOp(",")) Take();
        }
        if (Cur.Type == Tt.Eof) throw new ParseHardError($"{_path}:{line}: unterminated parameter list of {name}");
        Take(); // ')'
        // C++ member-initializer list: : a_(x), b_(y) …
        if (IsOp(":"))
        {
            while (Cur.Type != Tt.Eof && !IsOp("{") && !IsOp(";"))
            {
                if (IsOp("(")) { Take(); SkipBalanced('(', ')'); continue; }
                Take();
            }
        }
        // trailing specifiers: const / noexcept / override / final / -> type
        while (Cur.Type == Tt.Ident && Cur.Text is "const" or "noexcept" or "override" or "final")
            Take();
        if (IsOp("->"))
        {
            Take();
            while (Cur.Type != Tt.Eof && !IsOp("{") && !IsOp(";")) Take();
        }
        if (IsOp(";")) { Take(); return null; } // prototype or declaration
        if (!IsOp("{")) { SkipToSemicolonOrBrace(); if (IsOp(";")) Take(); return null; }
        Take();
        var invocations = ParseBody();
        if (!IsOp("}")) throw new ParseHardError($"{_path}:{line}: unterminated body of {name}");
        Take();
        return new MethodDecl(name, scopeClass ?? "<global>", _ns, line, parameters,
            Array.Empty<AttributeDecl>(), invocations);
    }

    /// <summary>C++ class/struct with methods and constructors (TDS 5.2); null when there is no body.</summary>
    private TypeDecl? TryParseClass()
    {
        Take(); // class/struct/union keyword
        var name = Cur.Type == Tt.Ident ? Take().Text : "<anonymous>";
        while (Cur.Type != Tt.Eof && !IsOp("{") && !IsOp(";")) Take(); // bases etc.
        if (!IsOp("{")) { if (IsOp(";")) Take(); return null; }
        Take();
        var methods = new List<MethodDecl>();
        while (Cur.Type != Tt.Eof && !IsOp("}"))
        {
            if (Cur.Type is Tt.PreProc) { Take(); continue; }
            if (IsKw("public") || IsKw("private") || IsKw("protected"))
            {
                Take();
                if (IsOp(":")) Take();
                continue;
            }
            if (IsKw("class") || IsKw("struct") || IsKw("enum") || IsKw("union"))
            {
                SkipToSemicolonOrBrace();
                if (IsOp(";")) Take();
                continue;
            }
            if (IsKw("template")) { Take(); if (IsOp("<")) SkipBalanced('<', '>'); continue; }
            if (IsKw("using") || IsKw("typedef")) { SkipToSemicolonOrBrace(); if (IsOp(";")) Take(); continue; }
            // constructor: Name ( args ) [: inits] { body }
            if (Cur.Type == Tt.Ident && Cur.Text == name && PeekIsOpenParen(1))
            {
                var fn = TryParseFunctionDefinition(_pos, scopeClass: name);
                if (fn is not null) methods.Add(fn);
                continue;
            }
            // method: [type …] name(args) [spec] { body } | ;
            var probe = _pos;
            if (ScanToFunctionName(out var namePos))
            {
                var fn = TryParseFunctionDefinition(namePos, scopeClass: name);
                if (fn is not null) methods.Add(fn);
                else _pos = probe + 1;
                continue;
            }
            // data member or unknown — tolerate to next ; or }
            while (Cur.Type != Tt.Eof && !IsOp(";") && !IsOp("}"))
            {
                if (IsOp("(")) { Take(); SkipBalanced('(', ')'); continue; }
                if (IsOp("{")) { Take(); SkipBalancedBraces(); continue; }
                Take();
            }
            if (IsOp(";")) Take();
        }
        if (IsOp("}")) Take();
        if (IsOp(";")) Take();
        return new TypeDecl(name, _ns, methods);
    }

    /// <summary>Statement scan inside one function/macro body. Returns the collected invocations.</summary>
    private List<InvocationDecl> ParseBody()
    {
        var invocations = new List<InvocationDecl>();
        var varTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        _hoistedPort = null;
        _hoistedIp = null;
        ParseBlock(invocations, varTypes);
        return invocations;
    }

    private void ParseBlock(List<InvocationDecl> invocations, Dictionary<string, string> varTypes)
    {
        while (Cur.Type != Tt.Eof && !IsOp("}"))
        {
            if (Cur.Type is Tt.PreProc) { Take(); continue; }

            if (IsOp("{"))
            {
                Take();
                ParseBlock(invocations, varTypes);
                if (IsOp("}")) Take();
                continue;
            }
            if (IsOp(";")) { Take(); continue; }

            if (IsKw("if") || IsKw("while") || IsKw("for") || IsKw("switch") || IsKw("catch"))
            {
                Take();
                ScanCondition(invocations, varTypes);
                continue; // the body is handled by the generic '{' branch
            }
            if (IsKw("do") || IsKw("else") || IsKw("try")) { Take(); continue; }
            if (IsKw("return") || IsKw("throw") || IsKw("case"))
            {
                Take();
                ScanRestOfStatement(invocations, varTypes);
                continue;
            }

            ParseStatement(invocations, varTypes);
        }
    }

    /// <summary>Walk a control clause `(...)` collecting the calls inside (e.g. `if (connect(...) < 0)`).</summary>
    private void ScanCondition(List<InvocationDecl> invocations, Dictionary<string, string> varTypes)
    {
        if (!IsOp("(")) return;
        Take();
        int depth = 1;
        while (Cur.Type != Tt.Eof && depth > 0)
        {
            if (IsOp("(")) { depth++; Take(); continue; }
            if (IsOp(")")) { depth--; Take(); continue; }
            if (Cur.Type == Tt.Ident && PeekIsOpenParen(1) && !s_statementKeywords.Contains(Cur.Text))
            {
                RecordCall(invocations, varTypes);
                continue;
            }
            if (_isCpp && Cur.Type == Tt.Ident && NextIsQualifyOrMember())
            {
                RecordQualifiedOrMember(invocations, varTypes);
                continue;
            }
            Take();
        }
        if (Cur.Type == Tt.Eof) throw new ParseHardError($"{_path}:{Line}: unterminated control clause");
    }

    private void ScanRestOfStatement(List<InvocationDecl> invocations, Dictionary<string, string> varTypes)
    {
        while (Cur.Type != Tt.Eof && !IsOp(";") && !IsOp("}"))
        {
            if (IsOp("{"))
            {
                Take();
                ParseBlock(invocations, varTypes);
                if (IsOp("}")) Take();
                return;
            }
            if (Cur.Type == Tt.Ident && PeekIsOpenParen(1) && !s_statementKeywords.Contains(Cur.Text))
            {
                RecordCall(invocations, varTypes);
                continue;
            }
            if (_isCpp && Cur.Type == Tt.Ident && NextIsQualifyOrMember())
            {
                RecordQualifiedOrMember(invocations, varTypes);
                continue;
            }
            Take();
        }
        if (IsOp(";")) Take();
    }

    /// <summary>True when the current ident starts a `…::name(` or `…(->|.)name(` chain that ends in a call.</summary>
    private bool NextIsQualifyOrMember()
    {
        int i = _pos + 1;
        while (i < _toks.Count)
        {
            var t = _toks[i];
            if (t is { Type: Tt.Op, Text: "::" or "." or "->" }) { i++; continue; }
            if (t.Type == Tt.Ident)
            {
                // the chain continues while the ident is followed by a qualifier/member operator
                if (i + 1 < _toks.Count && _toks[i + 1] is { Type: Tt.Op, Text: "::" or "." or "->" })
                {
                    i += 2;
                    continue;
                }
                return i + 1 < _toks.Count && _toks[i + 1] is { Type: Tt.Op, Text: "(" };
            }
            return false;
        }
        return false;
    }

    private void ParseStatement(List<InvocationDecl> invocations, Dictionary<string, string> varTypes)
    {
        // socket-setup hoists (M5 modeling decision): X.sin_port = htons(N); X.sin_addr.s_addr = inet_addr("ip");
        if (TryHoist()) return;

        // bare call: NAME(args)…
        if (Cur.Type == Tt.Ident && !s_statementKeywords.Contains(Cur.Text) && PeekIsOpenParen(1))
        {
            RecordCall(invocations, varTypes);
            ScanRestOfStatement(invocations, varTypes);
            return;
        }

        // declaration or construction: [qual::]TYPE NAME ( args )…; or with an initializer.
        // Tried BEFORE qualified calls so `std::ifstream fs("p")` keeps its arguments;
        // ReadQualified restores position when the chain turns out to end in a call.
        if (Cur.Type == Tt.Ident && !s_statementKeywords.Contains(Cur.Text))
        {
            var save = _pos;
            var type = ReadQualified();
            if (type is not null && Cur.Type == Tt.Ident)
            {
                var varName = Cur.Text;
                if (PeekIsOpenParen(1))
                {
                    // construction: TYPE NAME(args)
                    Take(); // name
                    RecordConstruction(invocations, type, varName);
                    varTypes[varName] = ResolveAliasPrefix(type);
                    ScanRestOfStatement(invocations, varTypes);
                    return;
                }
                // plain declaration: TYPE NAME [= init] [, NAME …] ;
                varTypes[varName] = ResolveAliasPrefix(type);
                ScanRestOfStatement(invocations, varTypes);
                return;
            }
            _pos = save;
        }

        // qualified/member call: ns::fn(…) / obj.method(…)
        if (_isCpp && Cur.Type == Tt.Ident && NextIsQualifyOrMember())
        {
            RecordQualifiedOrMember(invocations, varTypes);
            ScanRestOfStatement(invocations, varTypes);
            return;
        }

        // expression statement / assignment: scan for calls, member chains, hoists
        ScanRestOfStatement(invocations, varTypes);
    }

    /// <summary>Resolve leading namespace aliases (fs::x → std::filesystem::x).</summary>
    private string ResolveAliasPrefix(string qualified)
    {
        int colon = qualified.IndexOf("::", StringComparison.Ordinal);
        var head = colon < 0 ? qualified : qualified[..colon];
        if (!_nsAliases.TryGetValue(head, out var target)) return qualified;
        return target + (colon < 0 ? "" : qualified[colon..]);
    }

    private void RecordConstruction(List<InvocationDecl> invocations, string type, string varName)
    {
        if (!IsOp("(")) return;
        var canonical = ResolveAliasPrefix(type);
        var line = Line;
        var args = CollectArgs(invocations: invocations, varTypes: null);
        invocations.Add(new InvocationDecl(
            ReceiverOfConstruction(canonical),
            LastSegment(canonical), line, args));
    }

    private static string? ReceiverOfConstruction(string canonicalType)
    {
        int colon = canonicalType.LastIndexOf("::", StringComparison.Ordinal);
        return colon > 0 ? canonicalType[..colon] : null;
    }

    private static string LastSegment(string qualified)
    {
        int colon = qualified.LastIndexOf("::", StringComparison.Ordinal);
        return colon < 0 ? qualified : qualified[(colon + 2)..];
    }

    private void RecordCall(List<InvocationDecl> invocations, Dictionary<string, string> varTypes)
    {
        var name = Cur.Text;
        var line = Line;
        Take();
        if (!IsOp("(")) return;
        var args = CollectArgs(invocations, varTypes);
        ApplyHoists(name, args);
        invocations.Add(new InvocationDecl(null, name, line, args));
    }

    /// <summary>obj.method(…) / ns::fn(…): receiver normalization for tracked variable types and aliases.</summary>
    private void RecordQualifiedOrMember(List<InvocationDecl> invocations, Dictionary<string, string> varTypes)
    {
        int start = _pos;
        var line = Line;
        var segments = new List<string> { Take().Text };
        bool viaQualify = false;
        while (Cur.Type != Tt.Eof && (IsOp("::") || IsOp(".") || IsOp("->")))
        {
            viaQualify |= IsOp("::");
            Take();
            if (Cur.Type != Tt.Ident) break;
            segments.Add(Take().Text);
        }
        if (segments.Count < 2 || !IsOp("("))
        {
            _pos = start;
            Take();
            return;
        }
        var method = segments[^1];
        if (viaQualify)
        {
            // qualified call: receiver = all-but-last segment chain, method = last
            var head = ResolveAliasPrefix(string.Join("::", segments[..^1]));
            var args = CollectArgs(invocations, varTypes);
            ApplyHoists(method, args);
            invocations.Add(new InvocationDecl(head, method, line, args));
            return;
        }
        // member call: receiver = tracked type when known, else the variable name
        var receiverName = string.Join(".", segments[..^1]);
        string receiver = varTypes.TryGetValue(receiverName, out var tracked)
            ? tracked
            : receiverName;
        var margs = CollectArgs(invocations, varTypes);
        ApplyHoists(method, margs);
        invocations.Add(new InvocationDecl(receiver, method, line, margs));
    }

    /// <summary>connect/listen receive the hoisted socket literals as synthetic named args (documented M5 decision).</summary>
    private void ApplyHoists(string method, List<CallArg> args)
    {
        if (method is not ("connect" or "listen")) return;
        if (_hoistedIp is not null) args.Add(new CallArg(_hoistedIp.Kind, _hoistedIp.Value, "host"));
        if (_hoistedPort is not null) args.Add(new CallArg(_hoistedPort.Kind, _hoistedPort.Value, "port"));
    }

    /// <summary>Recognize `X.sin_port = htons(N)` / `X.sin_addr.s_addr = inet_addr("ip")` and hoist the literal.</summary>
    private bool TryHoist()
    {
        if (Cur.Type != Tt.Ident || _pos + 10 >= _toks.Count) return false;
        // X.sin_port = htons( N );
        int i = _pos + 1;
        if (MatchDotField(i, "sin_port", out i)
            && _toks[i] is { Type: Tt.Op, Text: "=" }
            && _toks[i + 1] is { Type: Tt.Ident, Text: "htons" }
            && _toks[i + 2] is { Type: Tt.Op, Text: "(" }
            && _toks[i + 3] is { Type: Tt.Number } n
            && _toks[i + 4] is { Type: Tt.Op, Text: ")" })
        {
            _hoistedPort = new CallArg(ArgKind.Literal, n.Text);
            _pos = i + 5;
            if (IsOp(";")) Take();
            return true;
        }
        // X.sin_addr.s_addr = inet_addr( "ip" );
        int j = _pos + 1;
        if (MatchDotField(j, "sin_addr", out j)
            && MatchDotField(j, "s_addr", out j)
            && _toks[j] is { Type: Tt.Op, Text: "=" }
            && _toks[j + 1] is { Type: Tt.Ident, Text: "inet_addr" }
            && _toks[j + 2] is { Type: Tt.Op, Text: "(" }
            && _toks[j + 3] is { Type: Tt.String } ip
            && _toks[j + 4] is { Type: Tt.Op, Text: ")" })
        {
            _hoistedIp = new CallArg(ArgKind.Literal, ip.Text);
            _pos = j + 5;
            if (IsOp(";")) Take();
            return true;
        }
        return false;
    }

    private bool MatchDotField(int i, string fieldName, out int next)
    {
        next = -1;
        if (i + 1 < _toks.Count
            && _toks[i] is { Type: Tt.Op, Text: "." or "->" }
            && _toks[i + 1] is { Type: Tt.Ident } fld
            && fld.Text == fieldName)
        {
            next = i + 2;
            return true;
        }
        return false;
    }

    /// <summary>Collect one argument list from `(` to the matching `)` (TDS 7.3 argument classification).</summary>
    private List<CallArg> CollectArgs(List<InvocationDecl>? invocations, Dictionary<string, string>? varTypes)
    {
        var args = new List<CallArg>();
        if (!IsOp("(")) return args;
        Take();
        while (Cur.Type != Tt.Eof && !IsOp(")"))
        {
            if (IsOp(",")) { Take(); continue; }
            var arg = ClassifyArg(invocations, varTypes);
            if (arg is not null) args.Add(arg);
        }
        if (Cur.Type == Tt.Eof) throw new ParseHardError($"{_path}:{Line}: unterminated argument list");
        Take(); // ')'
        return args;
    }

    private void ScanArgSpan()
    {
        int depth = 0;
        while (Cur.Type != Tt.Eof)
        {
            if (IsOp("(")) depth++;
            else if (IsOp(")"))
            {
                if (depth == 0) break;
                depth--;
            }
            else if (depth == 0 && IsOp(",")) break;
            Take();
        }
    }

    /// <summary>Single-token classification for macro argument lists.</summary>
    private (ArgKind Kind, string Value) ClassifyArgToken()
    {
        if (Cur.Type is Tt.String or Tt.Number or Tt.Char)
        {
            var t = Take();
            return (ArgKind.Literal, t.Text);
        }
        if (Cur.Type == Tt.Ident)
        {
            var sb = new StringBuilder();
            while (Cur.Type == Tt.Ident)
            {
                sb.Append(Cur.Text);
                Take();
                if (IsOp("::") && _isCpp) { sb.Append("::"); Take(); continue; }
                break;
            }
            return (ArgKind.Literal, sb.ToString());
        }
        // anything else: consume balanced and report dynamic
        Take();
        while (Cur.Type != Tt.Eof && !IsOp(",") && !IsOp(")"))
        {
            if (IsOp("(")) { Take(); SkipBalanced('(', ')'); continue; }
            Take();
        }
        return (ArgKind.Expression, "");
    }

    /// <summary>Classify one call argument (TDS 7.3): literal / identifier chain / flag mask / compound literal / initializer list / expression.</summary>
    private CallArg? ClassifyArg(List<InvocationDecl>? invocations, Dictionary<string, string>? varTypes)
    {
        if (Cur.Type is Tt.String or Tt.Number or Tt.Char)
        {
            var t = Take();
            // continued expression (string concatenation, arithmetic): the whole arg is dynamic
            if (IsOp("+"))
            {
                ScanToEndOfArg();
                return new CallArg(ArgKind.Expression, "");
            }
            return new CallArg(ArgKind.Literal, t.Text);
        }

        if (Cur.Type == Tt.Ident)
        {
            // qualified chain in expression position: a::b::c (aliases resolved)
            if (_isCpp)
            {
                var save = _pos;
                var chain = ReadQualified();
                if (chain is not null && chain.Contains("::", StringComparison.Ordinal))
                {
                    var arg = MergeMask(ResolveAliasPrefix(chain));
                    SkipSubscripts();
                    return arg;
                }
                _pos = save;
            }
            var first = Take().Text;
            var plain = MergeMask(first);
            // array subscript groups (`argv[0]`) stay identifier-dynamic
            SkipSubscripts();
            return plain;
        }

        // compound literal: ( TYPE… ) { init-list }  |  initializer list: { a, b, … }
        if (IsOp("(") && CompoundLiteralAhead())
        {
            SkipBalanced('(', ')'); // consumes from '(' through the matching ')'
            if (IsOp("{")) return ParseInitializerList();
            return new CallArg(ArgKind.Expression, "");
        }
        if (IsOp("{"))
        {
            return ParseInitializerList();
        }

        // parenthesized expression — look through for calls, then dynamic
        if (IsOp("("))
        {
            Take();
            int depth = 1;
            while (Cur.Type != Tt.Eof && depth > 0)
            {
                if (IsOp("(")) { depth++; Take(); continue; }
                if (IsOp(")")) { depth--; Take(); continue; }
                if (Cur.Type == Tt.Ident && PeekIsOpenParen(1) && invocations is not null)
                {
                    RecordCall(invocations, varTypes ?? new Dictionary<string, string>());
                    continue;
                }
                Take();
            }
            if (Cur.Type == Tt.Eof) throw new ParseHardError($"{_path}:{Line}: unterminated parenthesized expression");
            return new CallArg(ArgKind.Expression, "");
        }

        // operator prefix or anything else — consume one token
        if (Cur.Type != Tt.Eof) Take();
        return null;
    }

    /// <summary>Skip `[ … ]` subscript groups after an identifier argument.</summary>
    private void SkipSubscripts()
    {
        while (IsOp("["))
        {
            Take();
            int depth = 1;
            while (Cur.Type != Tt.Eof && depth > 0)
            {
                if (IsOp("[")) depth++;
                else if (IsOp("]")) depth--;
                Take();
            }
            if (Cur.Type == Tt.Eof) throw new ParseHardError($"{_path}:{Line}: unterminated subscript");
        }
    }

    /// <summary>Consume the remainder of the current argument up to `,` / `)` at depth 0.</summary>
    private void ScanToEndOfArg()
    {
        while (Cur.Type != Tt.Eof && !IsOp(",") && !IsOp(")"))
        {
            if (IsOp("(")) { Take(); SkipBalanced('(', ')'); continue; }
            if (IsOp("{")) { Take(); SkipBalancedBraces(); continue; }
            Take();
        }
        if (Cur.Type == Tt.Eof) throw new ParseHardError($"{_path}:{Line}: unterminated argument");
    }

    /// <summary>Compound-literal shape: the arg starts at `(` and the matching `)` is followed by `{`.</summary>
    private bool CompoundLiteralAhead()
    {
        int i = _pos;
        int depth = 0;
        while (i < _toks.Count)
        {
            var t = _toks[i];
            if (t is { Type: Tt.Op, Text: "(" }) depth++;
            else if (t is { Type: Tt.Op, Text: ")" })
            {
                depth--;
                if (depth == 0)
                    return i + 1 < _toks.Count && _toks[i + 1] is { Type: Tt.Op, Text: "{" };
            }
            else if (t.Type == Tt.Eof) return false;
            i++;
        }
        return false;
    }

    /// <summary>Initializer/compound-literal list: all-literal elements (NULL dropped) → List; literal-then-dynamic → PartialList (argv0 anchoring); else Expression.</summary>
    private CallArg ParseInitializerList()
    {
        if (!IsOp("{")) return new CallArg(ArgKind.Expression, "");
        Take();
        var elements = new List<string>();
        bool allLiteral = true;
        bool firstIsLiteral = true;
        while (Cur.Type != Tt.Eof && !IsOp("}"))
        {
            if (IsOp(",")) { Take(); continue; }
            if (Cur.Type == Tt.Ident && Cur.Text == "NULL"
                && (_pos + 1 >= _toks.Count || _toks[_pos + 1] is { Type: Tt.Op, Text: "," or "}" }))
            {
                Take(); // argv terminator — dropped
                continue;
            }
            var el = ClassifyArg(null, null);
            if (el is null) continue;
            if (el.IsLiteral)
            {
                elements.Add(el.Value);
            }
            else
            {
                if (elements.Count == 0) firstIsLiteral = false;
                allLiteral = false;
            }
        }
        if (Cur.Type == Tt.Eof) throw new ParseHardError($"{_path}:{Line}: unterminated initializer list");
        Take(); // '}'
        if (elements.Count == 0) return new CallArg(ArgKind.Expression, "");
        if (allLiteral) return new CallArg(ArgKind.List, string.Join('\u0001', elements));
        // argv0-anchoring (TDS 5.2): first element literal → anchored argv0
        if (firstIsLiteral) return new CallArg(ArgKind.PartialList, elements[0]);
        return new CallArg(ArgKind.Expression, "");
    }

    /// <summary>Merge `A | B | C` identifier masks into one Identifier arg (O_* flags, std::ios modes).</summary>
    private CallArg MergeMask(string first)
    {
        var parts = new List<string> { first };
        while (IsOp("|"))
        {
            Take();
            if (Cur.Type == Tt.Ident)
            {
                var save = _pos;
                if (_isCpp)
                {
                    var chain = ReadQualified();
                    if (chain is not null && chain.Contains("::", StringComparison.Ordinal))
                    {
                        parts.Add(ResolveAliasPrefix(chain));
                        continue;
                    }
                    _pos = save;
                }
                parts.Add(Take().Text);
            }
            else if (IsOp("("))
            {
                // (expr) inside a mask — dynamic
                Take();
                SkipBalanced('(', ')');
                parts.Add("<dynamic>");
            }
            else break;
        }
        return new CallArg(ArgKind.Identifier, string.Join('|', parts));
    }

    /// <summary>Read a `a::b::c` qualified name (with template-argument tolerance); null when not qualified.</summary>
    private string? ReadQualified()
    {
        if (Cur.Type != Tt.Ident) return null;
        var parts = new List<string> { Take().Text };
        bool qualified = false;
        while (_isCpp && IsOp("::"))
        {
            Take();
            if (Cur.Type != Tt.Ident) return null;
            parts.Add(Take().Text);
            qualified = true;
        }
        // template arguments: vector<int> v; — tolerate to the closing '>'
        if (IsOp("<"))
        {
            SkipBalanced('<', '>');
            parts[^1] += "<…>";
            if (Cur.Type == Tt.Ident) return string.Join("::", parts);
        }
        if (!qualified) return parts[0];
        return string.Join("::", parts);
    }

    private void SkipToSemicolonOrBrace()
    {
        while (Cur.Type != Tt.Eof && !IsOp(";") && !IsOp("{") && !IsOp("}"))
        {
            if (IsOp("(")) { Take(); SkipBalanced('(', ')'); continue; }
            Take();
        }
        if (IsOp(";")) Take();
    }

    private void SkipBalancedBraces()
    {
        int depth = 0;
        while (Cur.Type != Tt.Eof)
        {
            if (IsOp("{")) depth++;
            else if (IsOp("}"))
            {
                depth--;
                if (depth == 0) { Take(); return; }
            }
            Take();
        }
        throw new ParseHardError($"{_path}:{Line}: unbalanced braces");
    }

    private void SkipBalanced(char open, char close)
    {
        if (!(Cur.Type == Tt.Op && Cur.Text == open.ToString())) return;
        int depth = 0;
        while (Cur.Type != Tt.Eof)
        {
            if (Cur.Type == Tt.Op && Cur.Text == open.ToString()) depth++;
            else if (Cur.Type == Tt.Op && Cur.Text == close.ToString())
            {
                depth--;
                if (depth == 0) { Take(); return; }
            }
            else if (open == '<' && IsOp(">>"))
            {
                // nested template closers (`vector<vector<int>>`) tokenize as one ">>"
                depth -= depth >= 2 ? 2 : 1;
                Take();
                if (depth <= 0) return;
                continue;
            }
            else if (open == '<' && IsOp(">"))
            {
                depth--;
                Take();
                if (depth <= 0) return;
                continue;
            }
            Take();
        }
        throw new ParseHardError($"{_path}:{Line}: unbalanced '{open}{close}'");
    }
}
