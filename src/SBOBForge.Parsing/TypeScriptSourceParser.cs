using System.Text;
using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// Deterministic hand-rolled JavaScript/TypeScript source parser (TDS 5.2 / 7):
/// tokenizer + lightweight structural parser producing the language-neutral
/// CodeModel. Pinned as lock grammar "typescript" (backend
/// "sbobforge-typescript"; covers .js/.mjs/.cjs/.ts/.tsx).
/// Scope: imports, module functions (function decls, const-arrow/function
/// assignments), class methods, and Jest/Vitest entry calls —
/// describe/it/test (with .skip/.only/.skipIf/.each, xit/xdescribe) — which
/// are synthesized into MethodDecls carrying a "js.entry" attribute consumed
/// by the adapters. describe-body statements outside entries and hooks are
/// not modeled (same posture as Python module-level statements); hook
/// callbacks (beforeEach etc.) are synthesized as attribute-less helpers so
/// their behavior surfaces as coverage noise, never silently dropped.
/// Unterminated strings/braces are hard errors (TDS 13). Regex literals use
/// the standard expression-position heuristic; TS type syntax is tolerated
/// and skipped, not modeled.
/// </summary>
public sealed class TypeScriptSourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-typescript";
    public const string Version = "1.0.0";

    string ICodeModelParser.BackendId => BackendId;

    public CodeModel Parse(string relativePath, string sourceText)
    {
        var tokens = Tokenize(sourceText, relativePath);
        return new Parser(tokens, relativePath).ParseModule();
    }

    // ---- tokenizer -------------------------------------------------------

    private enum Tt { Ident, Keyword, Str, FStr, Regex, Number, Op, Eof }

    private sealed record Token(Tt Type, string Text, int Line);

    private static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal)
    {
        "abstract","any","as","async","await","break","case","catch","class","const","continue",
        "declare","default","delete","do","else","enum","export","extends","false","finally",
        "for","from","function","get","if","implements","import","in","instanceof","interface",
        "is","let","namespace","new","never","null","number","of","private","protected","public",
        "readonly","return","satisfies","set","static","string","super","switch","this","throw",
        "true","try","type","typeof","undefined","unknown","var","void","while","yield","override"
    };

    /// <summary>Keywords after which a '/' starts a regex literal (expression position).</summary>
    private static readonly HashSet<string> s_regexAllowedKeywords = new(StringComparer.Ordinal)
    {
        "return","typeof","case","in","of","new","delete","void","instanceof","do","else","yield","await","throw"
    };

    /// <summary>Ops after which a '/' starts a regex literal (expression position).</summary>
    private static readonly HashSet<string> s_regexAllowedOps = new(StringComparer.Ordinal)
    {
        "(","[","{",",",";",":","=","==","===","!=","!==","+","-","*","/","%","&","|","^","!","~",
        "<",">","<=",">=","&&","||","??","=>","++","--","+=","-=","?.","..."
    };

    private static List<Token> Tokenize(string src, string path)
    {
        var toks = new List<Token>();
        int i = 0, line = 1;
        Tt lastType = Tt.Eof;
        string lastText = "";
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
            if (c == '/' && RegexAllowed(lastType, lastText))
            {
                int startLine = line;
                // scan regex body: until unescaped '/' outside a [...] class
                int j = i + 1;
                bool inClass = false;
                while (j < src.Length && (src[j] != '/' || inClass))
                {
                    if (src[j] == '\\') { j += 2; continue; }
                    if (src[j] == '[') inClass = true;
                    else if (src[j] == ']') inClass = false;
                    else if (src[j] == '\n') break;
                    j++;
                }
                if (j >= src.Length || src[j] != '/' || j == i + 1)
                    throw new ParseHardError($"{path}:{startLine}: unterminated regex literal");
                j++; // flags
                while (j < src.Length && (char.IsLetter(src[j]) || src[j] == '_')) j++;
                toks.Add(new Token(Tt.Regex, src[i..j], startLine));
                (lastType, lastText) = (Tt.Regex, src[i..j]);
                i = j;
                continue;
            }
            if (c is '"' or '\'')
            {
                var tok = ReadString(src, ref i, ref line, path, c);
                toks.Add(tok);
                (lastType, lastText) = (tok.Type, tok.Text);
                continue;
            }
            if (c == '`')
            {
                var tok = ReadTemplate(src, ref i, ref line, path);
                toks.Add(tok);
                (lastType, lastText) = (tok.Type, tok.Text);
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '$')) i++;
                string word = src[s..i];
                var type = s_keywords.Contains(word) ? Tt.Keyword : Tt.Ident;
                toks.Add(new Token(type, word, line));
                (lastType, lastText) = (type, word);
                continue;
            }
            if (char.IsDigit(c))
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '.' || src[i] == '_')) i++;
                toks.Add(new Token(Tt.Number, src[s..i], line));
                (lastType, lastText) = (Tt.Number, src[s..i]);
                continue;
            }
            string four = i + 3 < src.Length ? src.Substring(i, 4) : "";
            string three = i + 2 < src.Length ? src.Substring(i, 3) : "";
            string two = i + 1 < src.Length ? src.Substring(i, 2) : "";
            string op = four is ">>>= " ? four : "";
            if (four == ">>>=") { toks.Add(new(Tt.Op, four, line)); i += 4; }
            else if (three is "===" or "!==" or "**=" or "<<=" or ">>=" or "..." or "&&=" or "||=" or "??=")
            { toks.Add(new(Tt.Op, three, line)); i += 3; }
            else if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "??" or "?." or "=>" or "++"
                or "--" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<" or ">>"
                or "**" or "!!")
            { toks.Add(new(Tt.Op, two, line)); i += 2; }
            else { toks.Add(new Token(Tt.Op, c.ToString(), line)); i++; }
            (lastType, lastText) = (Tt.Op, toks[^1].Text);
        }
        toks.Add(new Token(Tt.Eof, "", line));
        return toks;
    }

    /// <summary>Classic JS lexer heuristic: a '/' starts a regex only in expression position (after operators, '(', ',', keywords like return — not after an identifier, literal, ')', ']' or '}').</summary>
    private static bool RegexAllowed(Tt lastType, string lastText) =>
        lastType == Tt.Eof
        || (lastType == Tt.Keyword && s_regexAllowedKeywords.Contains(lastText))
        || (lastType == Tt.Op && s_regexAllowedOps.Contains(lastText));

    private static Token ReadString(string src, ref int i, ref int line, string path, char quote)
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
                    'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', 'b' => '\b', 'f' => '\f',
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
        return new Token(Tt.Str, sb.ToString(), startLine);
    }

    /// <summary>Template literal: interpolated (FStr, value unresolvable → tier Q) unless free of '${'.</summary>
    private static Token ReadTemplate(string src, ref int i, ref int line, string path)
    {
        int startLine = line;
        var sb = new StringBuilder();
        i++; // opening backtick
        bool interpolated = false;
        while (i < src.Length && src[i] != '`')
        {
            char c = src[i];
            if (c == '\\')
            {
                if (i + 1 >= src.Length) throw new ParseHardError($"{path}:{startLine}: unterminated template literal");
                char e = src[i + 1];
                sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '`' => '`', _ => e });
                i += 2;
                continue;
            }
            if (c == '$' && i + 1 < src.Length && src[i + 1] == '{')
            {
                interpolated = true;
                // skip the interpolation body (brace-balanced at the '${' level)
                int depth = 1;
                i += 2;
                while (i < src.Length && depth > 0)
                {
                    if (src[i] == '{') depth++;
                    else if (src[i] == '}') depth--;
                    else if (src[i] == '\n') line++;
                    i++;
                }
                continue;
            }
            if (c == '\n') line++;
            sb.Append(c);
            i++;
        }
        if (i >= src.Length) throw new ParseHardError($"{path}:{startLine}: unterminated template literal");
        i++; // closing backtick
        return new Token(interpolated ? Tt.FStr : Tt.Str, sb.ToString(), startLine);
    }

    // ---- parser ----------------------------------------------------------

    private sealed class Parser
    {
        private readonly List<Token> _toks;
        private readonly string _path;
        private int _pos;
        private readonly string _moduleStem;
        private readonly List<string> _imports = new();
        private readonly List<MethodDecl> _moduleMethods = new();
        private readonly List<TypeDecl> _classTypes = new();

        private static readonly HashSet<string> s_entryRoots = new(StringComparer.Ordinal) { "it", "test" };
        private static readonly HashSet<string> s_disabledRoots = new(StringComparer.Ordinal) { "xit", "xtest" };
        private static readonly HashSet<string> s_describeRoots = new(StringComparer.Ordinal) { "describe", "context", "suite" };
        private static readonly HashSet<string> s_disabledDescribeRoots = new(StringComparer.Ordinal) { "xdescribe", "xcontext", "xsuite" };
        private static readonly HashSet<string> s_modifiers = new(StringComparer.Ordinal) { "skip", "only", "skipIf", "each" };
        private static readonly HashSet<string> s_hooks = new(StringComparer.Ordinal)
        { "beforeEach", "afterEach", "beforeAll", "afterAll", "before", "after" };
        private static readonly HashSet<string> s_stmtPrefixKeywords = new(StringComparer.Ordinal)
        { "export", "declare", "default", "async", "static", "public", "private", "protected", "readonly", "abstract", "override", "declare" };

        public Parser(List<Token> toks, string path)
        {
            _toks = toks;
            _path = path;
            _moduleStem = ModuleStem(path);
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

        private Token Cur => _toks[_pos];
        private bool IsOp(string text) => Cur.Type == Tt.Op && Cur.Text == text;
        private Token Take() => _toks[_pos++];

        public CodeModel ParseModule()
        {
            ScanLevel(_toks, "", false);
            var types = new List<TypeDecl> { new(_moduleStem, null, _moduleMethods) };
            types.AddRange(_classTypes);
            return new CodeModel(_path, types, _imports);
        }

        // ---- statement-level scanning (module, describe callbacks) --------

        /// <summary>Scans a token range for declarations and entry calls. describe recursion carries the title path.</summary>
        private void ScanLevel(List<Token> toks, string describePath, bool describeSkipped)
        {
            int end = toks.Count;
            int i = 0;
            while (i < end)
            {
                var t = toks[i];
                if (t.Type == Tt.Keyword)
                {
                    switch (t.Text)
                    {
                        case "import":
                            i = ScanImport(toks, i);
                            continue;
                        case "function":
                            i = ScanFunction(toks, i);
                            continue;
                        case "class":
                            i = ScanClass(toks, i);
                            continue;
                        case "const" or "let" or "var":
                            i = ScanConst(toks, i);
                            continue;
                        case "interface" or "enum":
                            i = SkipDeclWithBody(toks, i);
                            continue;
                        case "type":
                            i = SkipAlias(toks, i);
                            continue;
                        default:
                            if (s_stmtPrefixKeywords.Contains(t.Text)) { i++; continue; }
                            i++;
                            continue;
                    }
                }
                if (t.Type == Tt.Ident && i + 1 < end && toks[i + 1].Type == Tt.Op && toks[i + 1].Text == "(")
                {
                    i = ScanCall(toks, i, describePath, describeSkipped);
                    continue;
                }
                if (t.Type == Tt.Ident && i + 2 < end && toks[i + 1].Type == Tt.Op && toks[i + 1].Text == "."
                    && toks[i + 2].Type == Tt.Ident && s_modifiers.Contains(toks[i + 2].Text)
                    && i + 3 < end && toks[i + 3].Type == Tt.Op && toks[i + 3].Text == "(")
                {
                    i = ScanModifierCall(toks, i, describePath, describeSkipped);
                    continue;
                }
                if (t.Type == Tt.Op && (t.Text == "{" || t.Text == "(" || t.Text == "["))
                {
                    i = SkipBalancedAt(toks, i);
                    continue;
                }
                i++;
            }
        }

        private int ScanCall(List<Token> toks, int i, string describePath, bool describeSkipped)
        {
            string name = toks[i].Text;
            int j = i + 1;
            var args = CollectCallArgs(toks, ref j);

            if (s_entryRoots.Contains(name))
                return SynthesizeEntry(toks, args, toks[i].Line, describePath, describeSkipped ? "skip" : "run", "", Array.Empty<string>(), j);
            if (s_disabledRoots.Contains(name))
                return SynthesizeEntry(toks, args, toks[i].Line, describePath, "skip", "", Array.Empty<string>(), j);
            if (s_disabledDescribeRoots.Contains(name))
                return ScanDescribeArgs(toks, args, toks[i].Line, describePath, describeSkipped, "skip", "", j);
            if (s_describeRoots.Contains(name))
                return ScanDescribeArgs(toks, args, toks[i].Line, describePath, describeSkipped, "run", "", j);
            if (s_hooks.Contains(name))
                return SynthesizeHook(name, args, toks[i].Line, j);
            if (name == "require" && args.Count > 0 && args[0].Count == 1 && args[0][0].Type == Tt.Str)
                _imports.Add(args[0][0].Text);
            return j;
        }

        private int ScanModifierCall(List<Token> toks, int i, string describePath, bool describeSkipped)
        {
            string root = toks[i].Text;
            string mod = toks[i + 2].Text;
            int j = i + 3;
            var args = CollectCallArgs(toks, ref j);

            switch (mod)
            {
                case "skip":
                    if (s_entryRoots.Contains(root))
                        return SynthesizeEntry(toks, args, toks[i].Line, describePath, "skip", "", Array.Empty<string>(), j);
                    if (s_describeRoots.Contains(root))
                        return ScanDescribeArgs(toks, args, toks[i].Line, describePath, describeSkipped, "skip", "", j);
                    return j;
                case "only":
                    if (s_entryRoots.Contains(root))
                        return SynthesizeEntry(toks, args, toks[i].Line, describePath, describeSkipped ? "skip" : "run", "", Array.Empty<string>(), j);
                    if (s_describeRoots.Contains(root))
                        return ScanDescribeArgs(toks, args, toks[i].Line, describePath, describeSkipped, "run", "", j);
                    return j;
                case "skipIf":
                {
                    string value = SkipIfValue(args);
                    // it.skipIf(cond)(title, cb): the chained call carries the entry args
                    if (j < toks.Count && toks[j].Type == Tt.Op && toks[j].Text == "(")
                    {
                        var outerArgs = CollectCallArgs(toks, ref j);
                        if (s_entryRoots.Contains(root))
                            return SynthesizeEntry(toks, outerArgs, toks[i].Line, describePath, "skipIf", value, Array.Empty<string>(), j);
                        if (s_describeRoots.Contains(root))
                            return ScanDescribeArgs(toks, outerArgs, toks[i].Line, describePath, describeSkipped, "skipIf", value, j);
                    }
                    return j;
                }
                case "each":
                {
                    if (!s_entryRoots.Contains(root)) return j;
                    var cases = EachCases(args);
                    // the returned function is called with (title, cb)
                    if (j < toks.Count && toks[j].Type == Tt.Op && toks[j].Text == "(")
                    {
                        var outerArgs = CollectCallArgs(toks, ref j);
                        return SynthesizeEntry(toks, outerArgs, toks[i].Line, describePath, describeSkipped ? "skip" : "run", "", cases, j);
                    }
                    return j;
                }
                default:
                    return j;
            }
        }

        /// <summary>describe(title, cb): recurse into callbacks carrying the title path; skipIf resolves at describe level.</summary>
        private int ScanDescribeArgs(List<Token> toks, List<List<Token>> args, int line, string describePath, bool describeSkipped, string state, string skipIfValue, int j)
        {
            string title = LiteralTitle(args);
            string path = describePath.Length > 0 ? describePath + " > " + title : title;
            bool skipped = describeSkipped
                || state == "skip"
                || (state == "skipIf" && skipIfValue == "True");
            foreach (var chunk in args)
                if (IsCallbackLike(chunk))
                    ScanLevel(ParseCallback(chunk).Body, path, skipped);
            return j;
        }

        private int SynthesizeEntry(List<Token> toks, List<List<Token>> args, int line, string describePath, string state, string skipIfValue, string[] cases, int j)
        {
            string title = LiteralTitle(args);
            if (title is null) return j;
            var callback = args.FirstOrDefault(IsCallbackLike);
            string full = describePath.Length > 0 ? describePath + " > " + title : title;
            var attrArgs = new List<AttributeArg>
            {
                new(ArgKind.Literal, title, "title"),
                new(ArgKind.Literal, full, "full"),
                new(ArgKind.Literal, state, "state"),
                new(ArgKind.Literal, skipIfValue, "skipIf")
            };
            if (cases.Length > 0)
                attrArgs.Add(new AttributeArg(ArgKind.List, string.Join('\u0001', cases), "cases"));

            var (parameters, body) = callback is null
                ? (new List<string>(), new List<Token>())
                : ParseCallback(callback);
            var method = new MethodDecl(
                full + "@" + line, _moduleStem, null, line, parameters,
                new[] { new AttributeDecl("js.entry", attrArgs) },
                ScanInvocations(body));
            _moduleMethods.Add(method);
            return j;
        }

        private int SynthesizeHook(string hookName, List<List<Token>> args, int line, int j)
        {
            foreach (var chunk in args)
            {
                if (!IsCallbackLike(chunk)) continue;
                var (_, body) = ParseCallback(chunk);
                _moduleMethods.Add(new MethodDecl(
                    hookName + "@" + line, _moduleStem, null, line, Array.Empty<string>(),
                    Array.Empty<AttributeDecl>(), ScanInvocations(body)));
            }
            return j;
        }

        private static string LiteralTitle(List<List<Token>> args) =>
            args.Count > 0 && args[0].Count == 1 && args[0][0].Type == Tt.Str ? args[0][0].Text : null ?? "";

        private static string SkipIfValue(List<List<Token>> args)
        {
            if (args.Count == 0) return "Other";
            var a = args[0];
            if (a.Count == 1 && a[0].Type == Tt.Keyword && a[0].Text == "true") return "True";
            if (a.Count == 1 && a[0].Type == Tt.Keyword && a[0].Text == "false") return "False";
            return "Other";
        }

        /// <summary>test.each([literals])(…): single-parameter fast path (TDS 7.2, same rule as pytest parametrize).</summary>
        private static string[] EachCases(List<List<Token>> args)
        {
            if (args.Count == 0) return Array.Empty<string>();
            var a = args[0];
            if (a.Count < 2 || a[0].Type != Tt.Op || a[0].Text != "[" || a[^1].Type != Tt.Op || a[^1].Text != "]")
                return Array.Empty<string>();
            var items = SplitTopLevel(a[1..^1], ",");
            if (items.Count == 0 || !items.All(x => x.Count == 1 && x[0].Type is Tt.Str or Tt.Number))
                return Array.Empty<string>();
            return items.Select(x => x[0].Text).ToArray();
        }

        private static bool IsCallbackLike(List<Token> chunk)
        {
            if (chunk.Count == 0) return false;
            if (chunk[0].Type == Tt.Keyword && (chunk[0].Text == "function" || chunk[0].Text == "async"))
            {
                var first = chunk[0].Text == "async" && chunk.Count > 1 && chunk[1].Type == Tt.Keyword && chunk[1].Text == "function"
                    ? chunk[1] : chunk[0];
                return first.Text == "function";
            }
            if (chunk[0].Type == Tt.Keyword && chunk[0].Text == "async")
            {
                // async (…) => …
                return chunk.Count > 1 && chunk[1].Type == Tt.Op && chunk[1].Text == "(";
            }
            if (chunk[0].Type == Tt.Op && chunk[0].Text == "(")
            {
                // (…) => …  — find matching ')' then '=>'
                int depth = 0;
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (chunk[i].Type == Tt.Op && chunk[i].Text == "(") depth++;
                    else if (chunk[i].Type == Tt.Op && chunk[i].Text == ")") { depth--; if (depth == 0) { } }
                    if (depth == 0 && chunk[i].Type == Tt.Op && chunk[i].Text == ")")
                    {
                        // skip optional ': type' then require '=>'
                        int k = i + 1;
                        int tdepth = 0;
                        while (k < chunk.Count && !(tdepth == 0 && chunk[k].Type == Tt.Op && chunk[k].Text == "=>"))
                        {
                            if (chunk[k].Type == Tt.Op && (chunk[k].Text == "<" || chunk[k].Text == "(" || chunk[k].Text == "[")) tdepth++;
                            else if (chunk[k].Type == Tt.Op && (chunk[k].Text == ">" || chunk[k].Text == ")" || chunk[k].Text == "]")) tdepth--;
                            k++;
                        }
                        return k < chunk.Count;
                    }
                }
            }
            // single ident => … (p => …)
            return chunk.Count > 1 && chunk[0].Type is Tt.Ident or Tt.Keyword
                && chunk[1].Type == Tt.Op && chunk[1].Text == "=>";
        }

        /// <summary>Callback chunk → (param names, body tokens). Expression bodies scan the remaining tokens.</summary>
        private (List<string> Parameters, List<Token> Body) ParseCallback(List<Token> chunk)
        {
            int i = 0;
            if (chunk[i].Type == Tt.Keyword && chunk[i].Text == "async") i++;
            if (chunk[i].Type == Tt.Keyword && chunk[i].Text == "function")
            {
                i++;
                // optional name and optional * generator
                if (chunk[i].Type == Tt.Op && chunk[i].Text == "*") i++;
                if (chunk[i].Type is Tt.Ident or Tt.Keyword) i++; // function name
                var (parameters, rest) = ParseParams(chunk, i);
                // expect '{' (maybe after return type)
                int b = rest;
                while (b < chunk.Count && !(chunk[b].Type == Tt.Op && chunk[b].Text == "{")) b++;
                if (b >= chunk.Count) return (parameters, new List<Token>());
                var inner = CollectBalancedFrom(chunk, b);
                return (parameters, inner);
            }
            if (chunk[i].Type == Tt.Op && chunk[i].Text == "(")
            {
                var (parameters, rest) = ParseParams(chunk, i);
                int k = rest;
                int depth = 0;
                while (k < chunk.Count)
                {
                    if (chunk[k].Type == Tt.Op && (chunk[k].Text == "<" || chunk[k].Text == "(" || chunk[k].Text == "[" || chunk[k].Text == "{")) depth++;
                    else if (chunk[k].Type == Tt.Op && (chunk[k].Text == ">" || chunk[k].Text == ")" || chunk[k].Text == "]")) depth--;
                    else if (depth == 0 && chunk[k].Type == Tt.Op && chunk[k].Text == "=>") break;
                    k++;
                }
                if (k >= chunk.Count) return (parameters, new List<Token>());
                int bodyStart = k + 1;
                if (bodyStart < chunk.Count && chunk[bodyStart].Type == Tt.Op && chunk[bodyStart].Text == "{")
                    return (parameters, CollectBalancedFrom(chunk, bodyStart));
                return (parameters, chunk[bodyStart..]);
            }
            // ident => body
            var name = chunk[0].Type is Tt.Ident or Tt.Keyword ? chunk[0].Text : "";
            var names = name.Length > 0 ? new List<string> { name } : new List<string>();
            if (2 < chunk.Count && chunk[2].Type == Tt.Op && chunk[2].Text == "{")
                return (names, CollectBalancedFrom(chunk, 2));
            return (names, chunk.Count > 2 ? chunk[3..] : new List<Token>());
        }

        /// <summary>Parameter list at '(' index i → (names, index after ')').</summary>
        private (List<string>, int) ParseParams(List<Token> chunk, int i)
        {
            var names = new List<string>();
            int depth = 0;
            int close = i;
            for (int k = i; k < chunk.Count; k++)
            {
                if (chunk[k].Type == Tt.Op && (chunk[k].Text == "(" || chunk[k].Text == "[" || chunk[k].Text == "{")) depth++;
                else if (chunk[k].Type == Tt.Op && (chunk[k].Text == ")" || chunk[k].Text == "]" || chunk[k].Text == "}"))
                {
                    depth--;
                    if (depth == 0) { close = k; break; }
                }
            }
            if (close <= i) return (names, i + 1);
            foreach (var part in SplitTopLevel(chunk[(i + 1)..close], ","))
            {
                if (part.Count == 0) continue;
                // strip '...' spread and TS 'this' parameters; name = first ident before any ':' type
                int p = 0;
                while (p < part.Count && part[p].Type == Tt.Op && part[p].Text == "...") p++;
                if (p < part.Count && part[p].Type is Tt.Ident or Tt.Keyword)
                {
                    if (part[p].Text != "this") names.Add(part[p].Text);
                }
                // destructured params: names inside brackets are not tracked (rare in entries)
            }
            return (names, close + 1);
        }

        /// <summary>Tokens strictly between the balanced region opened at i (opener/closer excluded).</summary>
        private List<Token> CollectBalancedFrom(List<Token> toks, int i)
        {
            var inner = new List<Token>();
            char open = toks[i].Text[0];
            char close = open == '{' ? '}' : open == '(' ? ')' : ']';
            int depth = 1;
            for (int k = i + 1; k < toks.Count; k++)
            {
                var t = toks[k];
                if (t.Type == Tt.Op && t.Text == open.ToString()) depth++;
                else if (t.Type == Tt.Op && t.Text == close.ToString())
                {
                    depth--;
                    if (depth == 0) return inner;
                }
                inner.Add(t);
            }
            throw new ParseHardError($"{_path}: unbalanced '{open}'");
        }

        // ---- declarations ----------------------------------------------------

        private int ScanImport(List<Token> toks, int i)
        {
            int startLine = toks[i].Line;
            i++; // import
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Str) { _imports.Add(t.Text); i++; continue; }
                if (t.Type == Tt.Keyword && t.Text == "from") { i++; continue; }
                if (t.Type == Tt.Op && t.Text == ";") return i + 1;
                if (t.Type == Tt.Op && (t.Text == "{" || t.Text == "(" || t.Text == "["))
                {
                    // import ( … ) / import { … } — balanced skip, but strings inside are modules too
                    int depth = 0;
                    char open = t.Text[0];
                    char close = open == '{' ? '}' : ')';
                    while (i < toks.Count)
                    {
                        var u = toks[i];
                        if (u.Type == Tt.Op && u.Text == open.ToString()) depth++;
                        else if (u.Type == Tt.Op && u.Text == close.ToString())
                        {
                            depth--;
                            i++;
                            if (depth == 0) break;
                            continue;
                        }
                        else if (u.Type == Tt.Str) _imports.Add(u.Text);
                        i++;
                    }
                    continue;
                }
                if (t.Line != startLine) return i;
                i++;
            }
            return i;
        }

        private int ScanFunction(List<Token> toks, int i)
        {
            i++; // function
            if (i < toks.Count && toks[i].Type == Tt.Op && toks[i].Text == "*") i++;
            if (i >= toks.Count || toks[i].Type is not (Tt.Ident or Tt.Keyword)) return i;
            int line = toks[i].Line;
            string name = toks[i].Text;
            i++;
            if (i < toks.Count && toks[i].Type == Tt.Op && toks[i].Text == "<")
                i = SkipGenerics(toks, i);
            if (i >= toks.Count || toks[i].Type != Tt.Op || toks[i].Text != "(") return i;
            var (parameters, rest) = ParseParams(toks, i);
            i = rest;
            // skip return type until '{' or end of statement
            while (i < toks.Count && !(toks[i].Type == Tt.Op && toks[i].Text == "{")) i++;
            if (i >= toks.Count) return i;
            var body = CollectBalancedFrom(toks, i);
            i = IndexPast(toks, i, "{");
            _moduleMethods.Add(new MethodDecl(name, _moduleStem, null, line, parameters,
                Array.Empty<AttributeDecl>(), ScanInvocations(body)));
            return i;
        }

        private int ScanClass(List<Token> toks, int i)
        {
            i++; // class
            if (i >= toks.Count || toks[i].Type is not (Tt.Ident or Tt.Keyword)) return i;
            string name = toks[i].Text;
            i++;
            while (i < toks.Count && !(toks[i].Type == Tt.Op && toks[i].Text == "{")) i++;
            if (i >= toks.Count) return i;
            var body = CollectBalancedFrom(toks, i);
            i = IndexPast(toks, i, "{");

            var methods = new List<MethodDecl>();
            int k = 0;
            while (k < body.Count)
            {
                var t = body[k];
                if (t.Type == Tt.Keyword && (s_stmtPrefixKeywords.Contains(t.Text) || t.Text is "get" or "set" or "async"))
                { k++; continue; }
                if (t.Type == Tt.Ident || (t.Type == Tt.Keyword && t.Text != "constructor" && t.Text == "constructor"))
                {
                    // method: name(params) [: type] { body }
                    if (k + 1 < body.Count && body[k + 1].Type == Tt.Op && body[k + 1].Text == "(")
                    {
                        int line = t.Line;
                        string mname = t.Text;
                        var (parameters, rest) = ParseParams(body, k + 1);
                        int b = rest;
                        while (b < body.Count && !(body[b].Type == Tt.Op && body[b].Text == "{")
                               && !(body[b].Type == Tt.Op && body[b].Text == ";")) b++;
                        if (b < body.Count && body[b].Type == Tt.Op && body[b].Text == "{")
                        {
                            var inner = CollectBalancedFrom(body, b);
                            // advance k past the method body within body tokens
                            k = AdvancePast(body, k, "{", inner.Count + 2);
                            methods.Add(new MethodDecl(mname, name, null, line, parameters,
                                Array.Empty<AttributeDecl>(), ScanInvocations(inner)));
                            continue;
                        }
                        k = rest;
                        continue;
                    }
                    // field: skip to ';' or line change
                    k = SkipField(body, k);
                    continue;
                }
                if (t.Type == Tt.Op && (t.Text == "{" || t.Text == "(" || t.Text == "["))
                {
                    k = SkipBalancedWithin(body, k);
                    continue;
                }
                k++;
            }
            _classTypes.Add(new TypeDecl(name, null, methods));
            return i;
        }

        private int ScanConst(List<Token> toks, int i)
        {
            int startLine = toks[i].Line;
            i++; // const/let/var
            if (i < toks.Count && (toks[i].Type == Tt.Op && (toks[i].Text == "{" || toks[i].Text == "[")))
            {
                // destructuring — not a method; skip the statement (imports handled by require scan)
                return SkipStatement(toks, i);
            }
            if (i >= toks.Count || toks[i].Type is not (Tt.Ident or Tt.Keyword)) return i;
            int line = toks[i].Line;
            string name = toks[i].Text;
            i++;
            // skip ': type'
            while (i < toks.Count && !(toks[i].Type == Tt.Op && (toks[i].Text == "=" || toks[i].Text == ";"))
                   && toks[i].Line == line) i++;
            if (i >= toks.Count || toks[i].Type != Tt.Op || toks[i].Text != "=") return i;
            i++;
            // function-like initializer?
            if (i < toks.Count)
            {
                var next = toks[i];
                bool fn = (next.Type == Tt.Keyword && (next.Text == "function" || next.Text == "async"))
                    || (next.Type == Tt.Op && next.Text == "(")
                    || (next.Type is Tt.Ident or Tt.Keyword && i + 1 < toks.Count
                        && toks[i + 1].Type == Tt.Op && toks[i + 1].Text == "=>")
                    || (next.Type == Tt.Keyword && next.Text == "async" && i + 1 < toks.Count
                        && toks[i + 1].Type == Tt.Op && toks[i + 1].Text == "(");
                if (fn)
                {
                    // gather the initializer chunk: to end of statement (a close
                    // alone does not end it — arrow params are followed by => body)
                    int end = i;
                    int depth = 0;
                    while (end < toks.Count)
                    {
                        var t = toks[end];
                        if (t.Type == Tt.Op && (t.Text == "{" || t.Text == "(" || t.Text == "[")) depth++;
                        else if (t.Type == Tt.Op && (t.Text == "}" || t.Text == ")" || t.Text == "]"))
                            depth--;
                        else if (depth == 0 && t.Type == Tt.Op && t.Text == ";") break;
                        else if (depth == 0 && t.Line != line && end > i) break;
                        end++;
                    }
                    var chunk = toks[i..end];
                    if (IsCallbackLike(chunk))
                    {
                        var (parameters, body) = ParseCallback(chunk);
                        _moduleMethods.Add(new MethodDecl(name, _moduleStem, null, line, parameters,
                            Array.Empty<AttributeDecl>(), ScanInvocations(body)));
                        return end;
                    }
                    return SkipStatement(toks, i - 1);
                }
            }
            return SkipStatement(toks, i - 1);
        }

        private int SkipStatement(List<Token> toks, int i)
        {
            // skip to ';' or line change at depth 0, recording bare require("…") imports
            int startLine = toks[i].Line;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Op && (t.Text == "{" || t.Text == "(" || t.Text == "["))
                {
                    int depth = 0;
                    char open = t.Text[0];
                    char close = open == '{' ? '}' : open == '(' ? ')' : ']';
                    int j = i;
                    while (j < toks.Count)
                    {
                        var u = toks[j];
                        if (u.Type == Tt.Op && u.Text == open.ToString()) depth++;
                        else if (u.Type == Tt.Op && u.Text == close.ToString())
                        {
                            depth--;
                            j++;
                            if (depth == 0) break;
                            continue;
                        }
                        j++;
                    }
                    i = j;
                    continue;
                }
                if (t.Type == Tt.Op && t.Text == ";") return i + 1;
                if (t.Line != startLine) return i;
                i++;
            }
            return i;
        }

        private int SkipDeclWithBody(List<Token> toks, int i)
        {
            while (i < toks.Count && !(toks[i].Type == Tt.Op && toks[i].Text == "{")) i++;
            if (i >= toks.Count) return i;
            return SkipBalancedAt(toks, i);
        }

        private int SkipAlias(List<Token> toks, int i)
        {
            int startLine = toks[i].Line;
            i++;
            while (i < toks.Count)
            {
                if (toks[i].Type == Tt.Op && (toks[i].Text == "{" || toks[i].Text == "(" || toks[i].Text == "["))
                { i = SkipBalancedAt(toks, i); continue; }
                if (toks[i].Type == Tt.Op && toks[i].Text == ";") return i + 1;
                if (toks[i].Line != startLine) return i;
                i++;
            }
            return i;
        }

        private int SkipGenerics(List<Token> toks, int i)
        {
            int depth = 0;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Op && t.Text == "<") depth++;
                else if (t.Type == Tt.Op && t.Text == ">") { depth--; i++; if (depth == 0) return i; continue; }
                i++;
            }
            return i;
        }

        private int SkipBalancedAt(List<Token> toks, int i)
        {
            char open = toks[i].Text[0];
            char close = open == '{' ? '}' : open == '(' ? ')' : ']';
            int depth = 0;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Op && t.Text == open.ToString()) depth++;
                else if (t.Type == Tt.Op && t.Text == close.ToString())
                {
                    depth--;
                    i++;
                    if (depth == 0) return i;
                    continue;
                }
                i++;
            }
            throw new ParseHardError($"{_path}: unbalanced '{open}'");
        }

        private int SkipBalancedWithin(List<Token> body, int k)
        {
            char open = body[k].Text[0];
            char close = open == '{' ? '}' : open == '(' ? ')' : ']';
            int depth = 0;
            while (k < body.Count)
            {
                var t = body[k];
                if (t.Type == Tt.Op && t.Text == open.ToString()) depth++;
                else if (t.Type == Tt.Op && t.Text == close.ToString())
                {
                    depth--;
                    k++;
                    if (depth == 0) return k;
                    continue;
                }
                k++;
            }
            throw new ParseHardError($"{_path}: unbalanced '{open}'");
        }

        private static int IndexPast(List<Token> toks, int i, string opener)
        {
            // i points at the opener; return index just past its matching closer
            char open = opener[0];
            char close = open == '{' ? '}' : open == '(' ? ')' : ']';
            int depth = 0;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Op && t.Text == open.ToString()) depth++;
                else if (t.Type == Tt.Op && t.Text == close.ToString())
                {
                    depth--;
                    i++;
                    if (depth == 0) return i;
                    continue;
                }
                i++;
            }
            return i;
        }

        private int AdvancePast(List<Token> body, int k, string opener, int tokenCount)
        {
            for (int n = 0; n < tokenCount && k < body.Count; n++) k++;
            return k;
        }

        private int SkipField(List<Token> body, int k)
        {
            int startLine = body[k].Line;
            while (k < body.Count)
            {
                var t = body[k];
                if (t.Type == Tt.Op && (t.Text == "{" || t.Text == "(" || t.Text == "["))
                    return SkipBalancedWithin(body, k);
                if (t.Type == Tt.Op && t.Text == ";") return k + 1;
                if (t.Line != startLine) return k;
                k++;
            }
            return k;
        }

        // ---- body scanning ---------------------------------------------------

        internal List<InvocationDecl> ScanInvocations(List<Token> body)
        {
            var invocations = new List<InvocationDecl>();
            int i = 0;
            while (i < body.Count)
            {
                var t = body[i];
                if (t.Type is not (Tt.Ident or Tt.Keyword)) { i++; continue; }
                if (i + 1 < body.Count && body[i + 1].Type == Tt.Op && body[i + 1].Text == "(")
                {
                    string? receiver = null;
                    if (i > 0 && body[i - 1].Type == Tt.Op && body[i - 1].Text == ".")
                        receiver = WalkReceiverChain(body, i);
                    int j = i + 1;
                    var argChunks = CollectCallArgs(body, ref j);
                    if (t.Type == Tt.Ident || t.Text != "function")
                        invocations.Add(new InvocationDecl(receiver, t.Text, t.Line, argChunks.Select(ClassifyArg).ToList()));
                    foreach (var a in argChunks) invocations.AddRange(ScanInvocations(a));
                    i = j;
                    continue;
                }
                i++;
            }
            return invocations;
        }

        /// <summary>Dotted receiver chain immediately left of the method name (fs.readFileSync → fs; server.listen → server).</summary>
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
                if (tk.Type is Tt.Ident or Tt.Keyword && depth == 0)
                {
                    chain.Insert(0, tk.Text);
                    if (k - 1 >= 0 && body[k - 1].Type == Tt.Op && body[k - 1].Text == ".") { k--; continue; }
                    break;
                }
                break;
            }
            return chain.Count > 0 ? string.Join(".", chain) : null;
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

        internal CallArg ClassifyArg(List<Token> toks)
        {
            if (toks.Count == 0) return new CallArg(ArgKind.Expression, "");
            if (toks.Count == 1)
            {
                var t = toks[0];
                return t.Type switch
                {
                    Tt.Str or Tt.Number => CallArg.Literal(t.Text),
                    Tt.FStr => new CallArg(ArgKind.Interpolated, ""),
                    Tt.Regex => new CallArg(ArgKind.Expression, ""),
                    Tt.Ident => new CallArg(ArgKind.Identifier, t.Text),
                    Tt.Keyword when t.Text is "true" or "false" or "null" or "undefined"
                        => new CallArg(ArgKind.Identifier, t.Text),
                    _ => new CallArg(ArgKind.Expression, "")
                };
            }
            // dotted identifier chain a.b.c
            if (toks.Count % 2 == 1 && toks.All(t => t.Type is Tt.Ident or Tt.Keyword
                || (t.Type == Tt.Op && t.Text == ".")))
                return new CallArg(ArgKind.Identifier, string.Join('.', toks.Where(t => t.Type is Tt.Ident or Tt.Keyword).Select(t => t.Text)));

            // string concat of literals: "a" + "b"
            if (IsStringConcat(toks, out string? concat) && concat != null)
                return CallArg.Literal(concat);

            // array literal of literals → List; anchored first element → PartialList (argv0-anchoring, TDS 5.2)
            if (toks[0].Type == Tt.Op && toks[0].Text == "[" && toks[^1].Type == Tt.Op && toks[^1].Text == "]")
            {
                var items = SplitTopLevel(toks[1..^1], ",");
                if (items.Count > 0 && items.All(x => x.Count == 1 && x[0].Type is Tt.Str or Tt.Number))
                    return new CallArg(ArgKind.List, string.Join('\u0001', items.Select(x => x[0].Text)));
                if (items.Count > 0 && items[0].Count == 1 && items[0][0].Type is Tt.Str or Tt.Number)
                    return new CallArg(ArgKind.PartialList, items[0][0].Text);
            }
            return new CallArg(ArgKind.Expression, "");
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
                    if (toks[i].Type != Tt.Str) return false;
                    parts.Add(toks[i].Text);
                }
                else if (toks[i].Type != Tt.Op || toks[i].Text != "+") return false;
            }
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
