using System.Text;
using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// Deterministic PowerShell structural parser (TDS 5.4 Phase B): the fixed
/// call-shape scanner of Phase A (Pester v5 `Describe`/`Context`/`It [-Skip]`
/// blocks, command invocations with `-Param value` named arguments, the call
/// operator `&amp;`, `New-Object TypeName(…)` / `[Type]::new(…)` construction,
/// and `&gt;`/`&gt;&gt;` redirections) is retained unchanged and driven by a
/// structural statement pass modeling `function`/`filter`/`workflow` blocks
/// (with `param(…)` and legacy `(...)` parameter lists), classes and enums,
/// `if`/`elseif`/`else`, `switch`, `foreach`/`for`/`while`, `do`, `try`/
/// `catch`/`finally`, `trap`, here-strings and backtick line continuations.
/// Statements outside modeled blocks attribute to a `&lt;script&gt;` helper
/// method (harness noise). Pinned as lock grammar "powershell" (backend
/// "sbobforge-powershell"); the lock pins the sha256 of the authored
/// PowerShell 7.4 grammar (Minotaur-Grammars PR #196), embedded byte-identical
/// so `--frozen` verifies it without a network fetch. Unterminated strings or
/// unbalanced braces are hard errors (TDS 13).
/// </summary>
public sealed class PowerShellSourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-powershell";
    public const string Version = "2.0.0";

    string ICodeModelParser.BackendId => BackendId;

    /// <summary>sha256 of the embedded PowerShell 7.4 grammar artifact (Minotaur-Grammars PR #196), pinned by the lock (TDS 5.4 Phase B).</summary>
    public static string GrammarSha256 => GrammarResource.Sha256(GrammarResource.PowerShellKey);

    public CodeModel Parse(string relativePath, string sourceText)
    {
        var tokens = Tokenize(sourceText, relativePath);
        return new Parser(tokens, relativePath).ParseFile();
    }

    // ---- tokenizer -------------------------------------------------------

    internal enum Tt { Ident, Param, Var, String, DQString, Number, Op, Eof }

    internal sealed record Token(Tt Type, string Text, int Line, bool Interp = false);

    private static List<Token> Tokenize(string src, string path)
    {
        var toks = new List<Token>();
        int i = 0, line = 1;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (c is ' ' or '\t' or '\r') { i++; continue; }
            // backtick line continuation: the newline is not counted, so the
            // continued statement stays on one logical line (TDS 5.4 Phase B)
            if (c == '`' && i + 1 < src.Length && (src[i + 1] == '\n' || src[i + 1] == '\r'))
            {
                i += (src[i + 1] == '\r' && i + 2 < src.Length && src[i + 2] == '\n') ? 3 : 2;
                continue;
            }
            if (c == '<' && src[i..].StartsWith("<#", StringComparison.Ordinal))
            {
                int close = src.IndexOf("#>", i + 2, StringComparison.Ordinal);
                if (close < 0) throw new ParseHardError($"{path}:{line}: unterminated block comment");
                line += src[i..(close + 2)].Count(ch => ch == '\n');
                i = close + 2;
                continue;
            }
            if (c == '#')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            // here-strings: @" … "@ / @' … '@ — opener immediately followed by a
            // line break, closer is q + '@' at the start of a later line
            if (c == '@' && i + 1 < src.Length && (src[i + 1] == '"' || src[i + 1] == '\''))
            {
                char q = src[i + 1];
                int rest = i + 2;
                if (rest < src.Length && (src[rest] == '\r' || src[rest] == '\n'))
                {
                    int startLine = line;
                    int body = rest;
                    if (src[body] == '\r' && body + 1 < src.Length && src[body + 1] == '\n') body += 2; else body++;
                    string closer = q + "@";
                    int end = -1, scan = body;
                    while (scan < src.Length)
                    {
                        int le = scan;
                        while (le < src.Length && src[le] != '\n') le++;
                        if (src[scan..le].TrimEnd('\r').TrimStart().StartsWith(closer, StringComparison.Ordinal))
                        { end = scan; break; }
                        if (le >= src.Length) break;
                        scan = le + 1;
                    }
                    if (end < 0) throw new ParseHardError($"{path}:{startLine}: unterminated here-string");
                    string text = src[body..end].TrimEnd('\r', '\n');
                    bool interp = false;
                    if (q == '"')
                        for (int k = 0; k + 1 < text.Length; k++)
                            if (text[k] == '$' && (char.IsLetter(text[k + 1]) || text[k + 1] is '{' or '('))
                            { interp = true; break; }
                    line += src[i..end].Count(ch => ch == '\n');
                    i = end;
                    // consume the closer line
                    while (i < src.Length && src[i] != '\n') i++;
                    if (i < src.Length) { i++; line++; }
                    toks.Add(q == '"'
                        ? new Token(Tt.DQString, text, startLine, interp)
                        : new Token(Tt.String, text, startLine));
                    continue;
                }
            }
            if (c == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (i < src.Length && src[i] != '\'')
                {
                    if (src[i] == '\n') throw new ParseHardError($"{path}:{line}: unterminated string literal");
                    if (src[i] == '\'' && i + 1 < src.Length && src[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                    sb.Append(src[i]); i++;
                }
                if (i >= src.Length) throw new ParseHardError($"{path}:{line}: unterminated string literal");
                i++;
                toks.Add(new Token(Tt.String, sb.ToString(), line));
                continue;
            }
            if (c == '"')
            {
                var (text, interp) = ReadDoubleQuoted(src, ref i, path, line);
                toks.Add(new Token(Tt.DQString, text, line, interp));
                continue;
            }
            if (c == '$')
            {
                int s = ++i;
                if (i < src.Length && src[i] == '{')
                {
                    int close = src.IndexOf('}', i);
                    if (close < 0) throw new ParseHardError($"{path}:{line}: unterminated variable");
                    var name = src[s..close];
                    line += src[s..(close + 1)].Count(ch => ch == '\n');
                    i = close + 1;
                    toks.Add(new Token(Tt.Var, "${" + name, line));
                    continue;
                }
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == ':' || src[i] == '.')) i++;
                if (i == s) { toks.Add(new Token(Tt.Op, "$", line)); continue; }
                toks.Add(new Token(Tt.Var, src[s..i], line));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '-' || src[i] == '.')) i++;
                toks.Add(new Token(Tt.Ident, src[s..i], line));
                continue;
            }
            if (c == '-' && i + 1 < src.Length && char.IsLetter(src[i + 1]))
            {
                int s = i;
                i++;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '.')) i++;
                toks.Add(new Token(Tt.Param, src[s..i], line));
                continue;
            }
            if (char.IsDigit(c))
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '.')) i++;
                toks.Add(new Token(Tt.Number, src[s..i], line));
                continue;
            }
            if (i + 2 < src.Length && src[i..].StartsWith(">>>", StringComparison.Ordinal))
            { toks.Add(new(Tt.Op, ">>>", line)); i += 3; continue; }
            string two = i + 1 < src.Length ? src.Substring(i, 2) : "";
            if (two is ">>" or "&&" or "||" or "==" or "!=" or "<=" or ">=" or "::" or "+=" or "-=" or "*=" or "/=" or "++" or "--")
            { toks.Add(new(Tt.Op, two, line)); i += 2; continue; }
            toks.Add(new Token(Tt.Op, c.ToString(), line));
            i++;
        }
        toks.Add(new Token(Tt.Eof, "", line));
        return toks;
    }

    private static (string Text, bool Interp) ReadDoubleQuoted(string src, ref int i, string path, int line)
    {
        i++; // opening quote
        var sb = new StringBuilder();
        bool interp = false;
        while (i < src.Length && src[i] != '"')
        {
            if (src[i] == '\n') throw new ParseHardError($"{path}:{line}: unterminated string literal");
            if (src[i] == '`' && i + 1 < src.Length)
            {
                char e = src[i + 1];
                sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', '`' => '`', '"' => '"', '$' => '$', _ => e });
                i += 2;
                continue;
            }
            if (src[i] == '$' && i + 1 < src.Length && (char.IsLetter(src[i + 1]) || src[i + 1] == '{' || src[i + 1] == '('))
                interp = true;
            sb.Append(src[i]); i++;
        }
        if (i >= src.Length) throw new ParseHardError($"{path}:{line}: unterminated string literal");
        i++; // closing quote
        return (sb.ToString(), interp);
    }

    // ---- structural pass (TDS 5.4 Phase B) -------------------------------

    /// <summary>Statements attributed to one method body: modeled parameters and lowered invocations.</summary>
    internal sealed class Scope
    {
        public List<string> Parameters { get; } = new();
        public List<InvocationDecl> Invocations { get; } = new();
    }

    private sealed class Parser
    {
        private readonly List<Token> _toks;
        private readonly string _path;
        private readonly List<MethodDecl> _methods = new();
        private readonly List<TypeDecl> _types = new(); // classes / enums
        private readonly List<string> _describeStack = new();

        public Parser(List<Token> toks, string path) { _toks = toks; _path = path; }

        private string PseudoTypeName() =>
            System.IO.Path.GetFileNameWithoutExtension(_path).Replace('.', '-');

        public CodeModel ParseFile()
        {
            var script = new Scope();
            ScanScope(_toks, script);
            // statements outside modeled blocks: harness/setup surface → helper noise rows
            if (script.Invocations.Count > 0)
                _methods.Add(new MethodDecl("<script>", PseudoTypeName(), null, 0,
                    Array.Empty<string>(), Array.Empty<AttributeDecl>(), script.Invocations));
            var mainType = new TypeDecl(PseudoTypeName(), null, _methods);
            return new CodeModel(_path, new[] { mainType }.Concat(_types).ToArray(), null);
        }

        // ---- scope scanning ------------------------------------------------

        private void ScanScope(List<Token> toks, Scope scope)
        {
            int i = 0;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Eof) break;
                if (t.Type == Tt.Op && (t.Text == "}" || t.Text == ")")) break; // enclosing group closer

                if (t.Type == Tt.Ident && (IsCommandCandidate(toks, i)
                        || (i > 0 && toks[i - 1].Type == Tt.Op && toks[i - 1].Text == "}")))
                {
                    // else / elseif / catch / finally are recognized right after a closed block
                    switch (t.Text)
                    {
                        case "using":
                            SkipToLineEnd(toks, ref i);
                            continue;
                        case "function":
                        case "filter":
                        case "workflow":
                            if (ParseFunction(toks, ref i, scope)) continue;
                            break;
                        case "class":
                            if (ParseClass(toks, ref i)) continue;
                            break;
                        case "enum":
                            if (ParseEnum(toks, ref i)) continue;
                            break;
                        case "param":
                            if (ParseParamBlock(toks, ref i, scope)) continue;
                            break;
                        case "if":
                        case "elseif":
                            ParseIf(toks, ref i, scope);
                            continue;
                        case "else":
                            if (ParseBlockAfterKeyword(toks, ref i, scope)) continue;
                            break;
                        case "switch":
                            ParseSwitch(toks, ref i, scope);
                            continue;
                        case "foreach":
                        case "for":
                        case "while":
                            ParseLoop(toks, ref i, scope);
                            continue;
                        case "do":
                            ParseDo(toks, ref i, scope);
                            continue;
                        case "try":
                            if (ParseBlockAfterKeyword(toks, ref i, scope)) continue;
                            break;
                        case "catch":
                        case "finally":
                            if (ParseCatchFinally(toks, ref i, scope)) continue;
                            break;
                        case "trap":
                            ParseTrap(toks, ref i, scope);
                            continue;
                        case "return":
                        case "exit":
                        case "throw":
                        case "break":
                        case "continue":
                            // the keyword itself is not an invocation; the remainder
                            // of the statement is scanned generically (e.g. the
                            // parenthesized group of `return (…)` is scanned)
                            i++;
                            continue;
                        case "Describe":
                        case "Context":
                            if (ParseDescribe(toks, ref i, scope)) continue;
                            break;
                        case "It":
                            if (ParseIt(toks, ref i)) continue;
                            break;
                    }
                }

                // brace / paren groups at a statement start
                if (t.Type == Tt.Op && t.Text == "{")
                {
                    var inner = CollectBalanced(toks, ref i, '{', '}');
                    ScanScope(inner, scope);
                    continue;
                }
                if (t.Type == Tt.Op && t.Text == "(")
                {
                    var inner = CollectBalanced(toks, ref i, '(', ')');
                    ScanScope(inner, scope);
                    continue;
                }

                var stmt = CollectStatement(toks, ref i);
                scope.Invocations.AddRange(StatementScanner.Scan(stmt));
            }
        }

        /// <summary>Mirrors the Phase A scanner's command-position rule, so keyword dispatch fires exactly where invocations start.</summary>
        private static bool IsCommandCandidate(List<Token> toks, int i)
        {
            var t = toks[i];
            if (i == 0) return true;
            var p = toks[i - 1];
            if (p.Type == Tt.Op && p.Text is ";" or "|" or "{" or "(" or "&&" or "||" or "=") return true;
            if (p.Type == Tt.Op && p.Text == ")" && p.Line < t.Line) return true;
            return p.Line < t.Line && p.Type != Tt.Op;
        }

        private static void SkipToLineEnd(List<Token> toks, ref int i)
        {
            int line = toks[i].Line;
            while (i < toks.Count && toks[i].Line == line && toks[i].Type != Tt.Eof) i++;
        }

        /// <summary>Collects one statement's tokens (same end rules as the Phase A arg scanner, so invocation boundaries are identical).</summary>
        private static List<Token> CollectStatement(List<Token> toks, ref int i)
        {
            var stmt = new List<Token>();
            if (i >= toks.Count || toks[i].Type == Tt.Eof) return stmt;
            stmt.Add(toks[i]);
            i++;
            while (i < toks.Count)
            {
                var a = toks[i];
                if (a.Type == Tt.Eof) break;
                if (a.Type == Tt.Op)
                {
                    if (a.Text == ";") { i++; break; } // separator consumed, statement ends
                    if (a.Text is "{" or "}" or ")") break; // block start / group closer
                }
                if (a.Type == Tt.Ident && a.Line > stmt[^1].Line) break; // new command word on a later line
                stmt.Add(a);
                i++;
            }
            return stmt;
        }

        private static List<Token> CollectBalanced(List<Token> toks, ref int i, char open, char close)
        {
            var inner = new List<Token>();
            i++; // opening bracket
            int depth = 1;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Eof) break;
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

        private static bool AtOp(List<Token> toks, int i, string text) =>
            i < toks.Count && toks[i].Type == Tt.Op && toks[i].Text == text;

        /// <summary>Variable name without the leading `$` / `${…}` decoration.</summary>
        private static string VarName(string tokenText)
        {
            var name = tokenText.TrimStart('$');
            if (name.StartsWith('{') && name.EndsWith('}')) name = name[1..^1];
            return name;
        }

        private static void AddParameterNames(List<Token> group, Scope scope)
        {
            // $Var tokens outside `= default` zones are parameter names;
            // [attributes], types and defaults are skipped.
            bool inDefault = false;
            foreach (var a in group)
            {
                if (a.Type == Tt.Op && a.Text == "=") inDefault = true;
                else if (a.Type == Tt.Op && (a.Text == "," || a.Text == ";")) inDefault = false;
                else if (!inDefault && a.Type == Tt.Var) scope.Parameters.Add(VarName(a.Text));
            }
        }

        // ---- keyword constructs --------------------------------------------

        /// <summary>function/filter/workflow Name [($a, $b)] { … } — legacy paren names and a `param(…)` block both model parameters.</summary>
        private bool ParseFunction(List<Token> toks, ref int i, Scope parentScope)
        {
            int keyword = i;
            i++;
            if (i >= toks.Count || toks[i].Type != Tt.Ident) { i = keyword; return false; }
            string name = toks[i].Text;
            int line = toks[i].Line;
            i++;
            var child = new Scope();
            if (AtOp(toks, i, "("))
            {
                var names = CollectBalanced(toks, ref i, '(', ')');
                foreach (var a in names)
                    if (a.Type == Tt.Var) child.Parameters.Add(VarName(a.Text));
            }
            if (!AtOp(toks, i, "{")) { i = keyword; return false; }
            var body = CollectBalanced(toks, ref i, '{', '}');
            ScanScope(body, child);
            _methods.Add(new MethodDecl(name, PseudoTypeName(), null, line,
                child.Parameters, Array.Empty<AttributeDecl>(), child.Invocations));
            return true;
        }

        /// <summary>param(…) block — models parameters; emits no invocation.</summary>
        private bool ParseParamBlock(List<Token> toks, ref int i, Scope scope)
        {
            int keyword = i;
            i++;
            if (!AtOp(toks, i, "(")) { i = keyword; return false; }
            var group = CollectBalanced(toks, ref i, '(', ')');
            AddParameterNames(group, scope);
            return true;
        }

        /// <summary>if/elseif (…) { … } — condition and body attribute to the same scope.</summary>
        private void ParseIf(List<Token> toks, ref int i, Scope scope)
        {
            i++;
            if (AtOp(toks, i, "("))
            {
                var cond = CollectBalanced(toks, ref i, '(', ')');
                ScanScope(cond, scope);
            }
            if (AtOp(toks, i, "{"))
            {
                var body = CollectBalanced(toks, ref i, '{', '}');
                ScanScope(body, scope);
            }
        }

        /// <summary>else { … } / try { … } — a block directly after the keyword.</summary>
        private bool ParseBlockAfterKeyword(List<Token> toks, ref int i, Scope scope)
        {
            int keyword = i;
            i++;
            if (!AtOp(toks, i, "{")) { i = keyword; return false; }
            var body = CollectBalanced(toks, ref i, '{', '}');
            ScanScope(body, scope);
            return true;
        }

        /// <summary>catch [type]/(types) { … } / finally { … } — recognized right after a closed block.</summary>
        private bool ParseCatchFinally(List<Token> toks, ref int i, Scope scope)
        {
            int keyword = i;
            i++;
            if (AtOp(toks, i, "[")) CollectBalanced(toks, ref i, '[', ']');
            else if (AtOp(toks, i, "(")) CollectBalanced(toks, ref i, '(', ')');
            if (!AtOp(toks, i, "{")) { i = keyword; return false; }
            var body = CollectBalanced(toks, ref i, '{', '}');
            ScanScope(body, scope);
            return true;
        }

        /// <summary>switch [-flags] (…) { pattern { … } default { … } } — conditions and case bodies attribute to the same scope.</summary>
        private void ParseSwitch(List<Token> toks, ref int i, Scope scope)
        {
            i++;
            while (i < toks.Count && toks[i].Type == Tt.Param) i++;
            if (AtOp(toks, i, "("))
            {
                var cond = CollectBalanced(toks, ref i, '(', ')');
                ScanScope(cond, scope);
            }
            if (!AtOp(toks, i, "{")) return;
            var body = CollectBalanced(toks, ref i, '{', '}');
            int j = 0;
            while (j < body.Count)
            {
                if (body[j].Type == Tt.Op && body[j].Text == "{")
                {
                    var caseBody = CollectBalanced(body, ref j, '{', '}');
                    ScanScope(caseBody, scope);
                    continue;
                }
                j++; // pattern / separator tokens
            }
        }

        /// <summary>foreach/for/while (…) { … } — condition and body attribute to the same scope.
        /// For foreach the `($x in &lt;collection&gt;)` loop-variable prefix is skipped so the
        /// collection expression itself is scanned in command position.</summary>
        private void ParseLoop(List<Token> toks, ref int i, Scope scope)
        {
            bool isForeach = toks[i].Text == "foreach";
            i++;
            if (AtOp(toks, i, "("))
            {
                var cond = CollectBalanced(toks, ref i, '(', ')');
                if (isForeach)
                {
                    int inPos = -1;
                    for (int j = 0; j < cond.Count; j++)
                    {
                        if (cond[j].Type == Tt.Ident && cond[j].Text == "in") { inPos = j; break; }
                    }
                    ScanScope(inPos >= 0 ? cond.Skip(inPos + 1).ToList() : cond, scope);
                }
                else
                {
                    ScanScope(cond, scope);
                }
            }
            if (AtOp(toks, i, "{"))
            {
                var body = CollectBalanced(toks, ref i, '{', '}');
                ScanScope(body, scope);
            }
        }

        /// <summary>do { … } [while/until (…)]</summary>
        private void ParseDo(List<Token> toks, ref int i, Scope scope)
        {
            i++;
            if (AtOp(toks, i, "{"))
            {
                var body = CollectBalanced(toks, ref i, '{', '}');
                ScanScope(body, scope);
            }
            if (i < toks.Count && toks[i].Type == Tt.Ident && toks[i].Text is "while" or "until")
            {
                i++;
                if (AtOp(toks, i, "("))
                {
                    var cond = CollectBalanced(toks, ref i, '(', ')');
                    ScanScope(cond, scope);
                }
            }
        }

        /// <summary>trap [type] { … }</summary>
        private void ParseTrap(List<Token> toks, ref int i, Scope scope)
        {
            i++;
            if (AtOp(toks, i, "[")) CollectBalanced(toks, ref i, '[', ']');
            if (AtOp(toks, i, "{"))
            {
                var body = CollectBalanced(toks, ref i, '{', '}');
                ScanScope(body, scope);
            }
        }

        /// <summary>class Name [: bases] { members } — methods become a TypeDecl; properties are skipped.</summary>
        private bool ParseClass(List<Token> toks, ref int i)
        {
            int keyword = i;
            i++;
            if (i >= toks.Count || toks[i].Type != Tt.Ident) { i = keyword; return false; }
            string name = toks[i].Text;
            i++;
            while (i < toks.Count && !AtOp(toks, i, "{") && toks[i].Type != Tt.Eof) i++; // base list / newlines
            if (!AtOp(toks, i, "{")) { i = keyword; return false; }
            var body = CollectBalanced(toks, ref i, '{', '}');
            var members = new List<MethodDecl>();
            int j = 0;
            while (j < body.Count)
            {
                var b = body[j];
                if (b.Type == Tt.Op && b.Text == "[") { CollectBalanced(body, ref j, '[', ']'); continue; }
                if (b.Type == Tt.Ident && (b.Text == "hidden" || b.Text == "static")) { j++; continue; }
                if (b.Type == Tt.Ident && j + 1 < body.Count && body[j + 1].Type == Tt.Op && body[j + 1].Text == "(")
                {
                    int line = b.Line;
                    string mname = b.Text;
                    j++;
                    var paramGroup = CollectBalanced(body, ref j, '(', ')');
                    var child = new Scope();
                    AddParameterNames(paramGroup, child);
                    while (j < body.Count && !AtOp(body, j, "{") && body[j].Type != Tt.Eof) j++; // `: ReturnType`
                    if (j < body.Count && AtOp(body, j, "{"))
                    {
                        var mbody = CollectBalanced(body, ref j, '{', '}');
                        ScanScope(mbody, child);
                        members.Add(new MethodDecl(mname, name, null, line,
                            child.Parameters, Array.Empty<AttributeDecl>(), child.Invocations));
                    }
                    continue;
                }
                j++; // properties / separators
            }
            _types.Add(new TypeDecl(name, null, members));
            return true;
        }

        /// <summary>enum Name { … } — a TypeDecl with no methods.</summary>
        private bool ParseEnum(List<Token> toks, ref int i)
        {
            int keyword = i;
            i++;
            if (i >= toks.Count || toks[i].Type != Tt.Ident) { i = keyword; return false; }
            string name = toks[i].Text;
            i++;
            if (!AtOp(toks, i, "{")) { i = keyword; return false; }
            CollectBalanced(toks, ref i, '{', '}');
            _types.Add(new TypeDecl(name, null, Array.Empty<MethodDecl>()));
            return true;
        }

        // ---- Pester blocks -------------------------------------------------

        /// <summary>Describe/Context "title" [-params] { … } — body recurses into the same scope; nested titles build the entry path.</summary>
        private bool ParseDescribe(List<Token> toks, ref int i, Scope scope)
        {
            if (i + 1 >= toks.Count || toks[i + 1].Type is not (Tt.String or Tt.DQString)) return false;
            string title = toks[i + 1].Text;
            int k = i + 2;
            while (k < toks.Count && toks[k].Type != Tt.Eof)
            {
                var a = toks[k];
                if (a.Type == Tt.Op && a.Text == "{")
                {
                    _describeStack.Add(title);
                    var inner = CollectBalanced(toks, ref k, '{', '}');
                    ScanScope(inner, scope);
                    _describeStack.RemoveAt(_describeStack.Count - 1);
                    i = k;
                    return true;
                }
                if (a.Type == Tt.Op && (a.Text == ";" || a.Text == "}" || a.Text == ")")) break;
                if (a.Type == Tt.Ident && a.Line > toks[k - 1].Line) break;
                k++;
            }
            return false; // not a block shape → generic command invocation
        }

        /// <summary>It "title" [-Skip] { … } → ps.entry-attributed method (same contract as Phase A).</summary>
        private bool ParseIt(List<Token> toks, ref int i)
        {
            if (i + 1 >= toks.Count || toks[i + 1].Type is not (Tt.String or Tt.DQString)) return false;
            var title = toks[i + 1].Text;
            int line = toks[i].Line;
            int k = i + 2;
            bool skip = false;
            while (k < toks.Count && toks[k].Type != Tt.Eof)
            {
                var a = toks[k];
                if (a.Type == Tt.Op && a.Text == "{")
                {
                    var body = CollectBalanced(toks, ref k, '{', '}');
                    var full = _describeStack.Count > 0
                        ? string.Join(" > ", _describeStack) + " > " + title
                        : title;
                    var attrs = new List<AttributeDecl>
                    {
                        new("ps.entry", new List<AttributeArg>
                        {
                            new(ArgKind.Literal, title, "title"),
                            new(ArgKind.Literal, full, "full"),
                            new(ArgKind.Literal, skip ? "skip" : "run", "state")
                        })
                    };
                    _methods.Add(new MethodDecl(title + "@" + line, PseudoTypeName(), null, line,
                        Array.Empty<string>(), attrs, StatementScanner.Scan(body)));
                    i = k;
                    return true;
                }
                if (a.Type == Tt.Op && (a.Text == ";" || a.Text == "}" || a.Text == ")")) break;
                if (a.Type == Tt.Param && a.Text == "-Skip") skip = true;
                if (a.Type == Tt.Ident && a.Line > toks[k - 1].Line) break;
                k++;
            }
            return false; // not a block shape → generic command invocation
        }
    }

    /// <summary>
    /// The fixed call-shape scanner (TDS 5.4 Phase A, retained): recognizes
    /// command words at statement starts with positional / `-Param value`
    /// named arguments, the `&amp;` call operator, `New-Object Type(…)` and
    /// `[Type]::new(…)` constructions, and `&gt;` / `&gt;&gt;` redirections.
    /// </summary>
    internal static class StatementScanner
    {
        internal static List<InvocationDecl> Scan(List<Token> toks)
        {
            var invocations = new List<InvocationDecl>();
            int i = 0;
            while (i < toks.Count)
            {
                var t = toks[i];
                int prev = i - 1;
                bool commandPosition =
                    prev < 0
                    || (toks[prev].Type == Tt.Op && toks[prev].Text is ";" or "|" or "{" or "(" or "&&" or "||" or "=")
                    || (t.Type == Tt.Ident && toks[prev].Type == Tt.Op && toks[prev].Text == ")" && toks[prev].Line < t.Line)
                    || (t.Type == Tt.Ident && toks[prev].Line < t.Line && toks[prev].Type != Tt.Op);

                // [Type]::new(args) at statement start
                if (commandPosition && t.Type == Tt.Op && t.Text == "[" && TryParseBracketNew(toks, ref i, invocations))
                    continue;

                // & "program" args — the call operator
                if (t.Type == Tt.Op && t.Text == "&" && commandPosition
                    && i + 1 < toks.Count && toks[i + 1].Type is Tt.String or Tt.DQString or Tt.Ident)
                {
                    i++;
                    var args = new List<CallArg>();
                    CollectArgs(toks, ref i, args, i - 1);
                    invocations.Add(new InvocationDecl(null, "&", t.Line, args));
                    CollectRedirection(toks, ref i, invocations, t.Line);
                    continue;
                }

                if (t.Type == Tt.Ident && commandPosition)
                {
                    int j = i;
                    var args = new List<CallArg>();
                    // New-Object TypeName(args) → Method = the short type name
                    if (t.Text == "New-Object" && i + 1 < toks.Count && toks[i + 1].Type == Tt.Ident)
                    {
                        string typeName = toks[i + 1].Text;
                        int line = t.Line;
                        j = i + 2;
                        if (j < toks.Count && toks[j].Type == Tt.Op && toks[j].Text == "(")
                        {
                            var ctorArgs = new List<CallArg>();
                            j++; // '('
                            while (j < toks.Count && !(toks[j].Type == Tt.Op && toks[j].Text == ")"))
                            {
                                if (toks[j].Type == Tt.Op && toks[j].Text == ",") { j++; continue; }
                                ctorArgs.Add(OneArg(toks, ref j));
                            }
                            j++; // ')'
                            invocations.Add(new InvocationDecl(null, ShortTypeName(typeName), line, ctorArgs));
                        }
                        else
                        {
                            invocations.Add(new InvocationDecl(null, ShortTypeName(typeName), line, Array.Empty<CallArg>()));
                        }
                        i = j;
                        continue;
                    }
                    j = i + 1; // arguments start after the command word
                    CollectArgs(toks, ref j, args, i);
                    invocations.Add(new InvocationDecl(null, t.Text, t.Line, args));
                    i = j;
                    CollectRedirection(toks, ref i, invocations, t.Line);
                    continue;
                }
                i++;
            }
            return invocations;
        }

        private static bool TryParseBracketNew(List<Token> toks, ref int i, List<InvocationDecl> invocations)
        {
            // [Full.Type.Name] :: new ( args )
            int j = i + 1;
            if (j >= toks.Count || toks[j].Type != Tt.Ident) return false;
            if (j + 1 >= toks.Count || toks[j + 1].Type != Tt.Op || toks[j + 1].Text != "]") return false;
            if (j + 2 >= toks.Count || toks[j + 2].Type != Tt.Op || toks[j + 2].Text != "::") return false;
            if (j + 3 >= toks.Count || toks[j + 3].Type != Tt.Ident || toks[j + 3].Text != "new") return false;
            int line = toks[j].Line;
            string typeName = toks[j].Text;
            j += 4;
            if (j >= toks.Count || toks[j].Type != Tt.Op || toks[j].Text != "(") return false;
            var ctorArgs = new List<CallArg>();
            j++;
            while (j < toks.Count && !(toks[j].Type == Tt.Op && toks[j].Text == ")"))
            {
                if (toks[j].Type == Tt.Op && toks[j].Text == ",") { j++; continue; }
                ctorArgs.Add(OneArg(toks, ref j));
            }
            j++; // ')'
            invocations.Add(new InvocationDecl(null, ShortTypeName(typeName), line, ctorArgs));
            i = j;
            return true;
        }

        private static string ShortTypeName(string typeName)
        {
            int dot = typeName.LastIndexOf('.');
            return dot >= 0 ? typeName[(dot + 1)..] : typeName;
        }

        /// <summary>Collects the arguments of a command invocation starting at j; on return j is past the last consumed arg. `commandStart` is the index of the command word itself; the new-line end-of-statement check only applies to tokens after it.</summary>
        private static void CollectArgs(List<Token> toks, ref int j, List<CallArg> args, int commandStart)
        {
            while (j < toks.Count)
            {
                var a = toks[j];
                // statement terminators
                if (a.Type == Tt.Op && (a.Text == ";" || a.Text == "|" || a.Text == "{" || a.Text == "&&" || a.Text == "||")) return;
                // a new command word starting on a later line ends the statement
                if (j > commandStart && a.Type == Tt.Ident && toks[j - 1].Line < a.Line) return;
                // redirection ends the arg list
                if (a.Type == Tt.Op && (a.Text == ">" || a.Text == ">>")) return;
                if (a.Type == Tt.Op && a.Text == "(" && j > 0)
                {
                    // subexpression / parenthesized expression: single Expression arg
                    int depth = 0;
                    int k = j;
                    while (k < toks.Count)
                    {
                        if (toks[k].Type == Tt.Op && toks[k].Text == "(") depth++;
                        else if (toks[k].Type == Tt.Op && toks[k].Text == ")") { depth--; if (depth == 0) { k++; break; } }
                        k++;
                    }
                    args.Add(new CallArg(ArgKind.Expression, ""));
                    j = k;
                    continue;
                }
                args.Add(OneArg(toks, ref j));
            }
        }

        private static CallArg OneArg(List<Token> toks, ref int j)
        {
            var a = toks[j];
            switch (a.Type)
            {
                case Tt.String:
                    j++;
                    return CallArg.Literal(a.Text);
                case Tt.DQString:
                    j++;
                    return a.Interp ? new CallArg(ArgKind.Interpolated, a.Text) : CallArg.Literal(a.Text);
                case Tt.Number:
                    j++;
                    return CallArg.Literal(a.Text);
                case Tt.Var:
                    j++;
                    return new CallArg(ArgKind.Identifier, a.Text);
                case Tt.Ident:
                    j++;
                    return new CallArg(ArgKind.Identifier, a.Text);
                case Tt.Param:
                    j++;
                    {
                        string name = a.Text[1..];
                        if (j < toks.Count && toks[j].Type is Tt.String or Tt.DQString or Tt.Number or Tt.Var or Tt.Ident)
                        {
                            var value = OneArg(toks, ref j);
                            return new CallArg(value.Kind, value.Value, name);
                        }
                        // switch parameter → boolean true
                        return new CallArg(ArgKind.Identifier, "true", name);
                    }
                default:
                    j++;
                    return new CallArg(ArgKind.Expression, "");
            }
        }

        /// <summary>`> path` / `>> path` after a command → synthesized open invocation.</summary>
        private static void CollectRedirection(List<Token> toks, ref int i, List<InvocationDecl> invocations, int line)
        {
            if (i < toks.Count && toks[i].Type == Tt.Op && (toks[i].Text == ">" || toks[i].Text == ">>"))
            {
                string op = toks[i].Text;
                i++;
                if (i < toks.Count && toks[i].Type is Tt.String or Tt.DQString or Tt.Number or Tt.Ident)
                {
                    var a = OneArg(toks, ref i);
                    invocations.Add(new InvocationDecl(null, op, line, new[] { a }));
                }
            }
        }
    }
}
