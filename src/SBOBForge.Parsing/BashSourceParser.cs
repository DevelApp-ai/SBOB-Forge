using System.Text;
using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// Deterministic Bash structural parser (TDS 5.4 Phase B): the fixed
/// call-shape scanner of Phase A (Bats `@test "title" { … }` entries with
/// the `skip` disable cue, bare command words with literal/variable
/// arguments, `&gt;` / `&gt;&gt;` redirection opens) is retained and driven by
/// a structural statement pass modeling `name() { … }` / `function name { … }`
/// blocks (recognized recursively at any nesting depth), `if`/`elif`/`else`/
/// `fi`, `while`/`until`, `for`/`select`, `case`/`esac`, `[[ … ]]` and
/// `[ … ]` tests, heredocs (`&lt;&lt;`, `&lt;&lt;-`), here-strings
/// (`&lt;&lt;&lt;`), fd-redirects (`2&gt;`, `&amp;&gt;` normalize to
/// truncate/append opens; fd-merge forms like `2&gt;&amp;1` emit no event),
/// and subshell/brace groups. Statements outside modeled blocks attribute to
/// a `&lt;script&gt;` helper method (harness noise); `$(…)` and backtick
/// substitutions remain opaque expressions. Pinned as lock grammar "bash"
/// (backend "sbobforge-bash"); the lock pins the sha256 of the authored
/// Bash 5 grammar (Minotaur-Grammars PR #196), embedded byte-identical so
/// frozen-mode verification needs no network fetch. Unterminated strings or
/// unbalanced braces are hard errors (TDS 13).
/// </summary>
public sealed class BashSourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-bash";
    public const string Version = "2.0.0";

    string ICodeModelParser.BackendId => BackendId;

    /// <summary>sha256 of the embedded Bash 5 grammar artifact (Minotaur-Grammars PR #196), pinned by the lock (TDS 5.4 Phase B).</summary>
    public static string GrammarSha256 => GrammarResource.Sha256(GrammarResource.BashKey);

    public CodeModel Parse(string relativePath, string sourceText)
    {
        var tokens = Tokenize(sourceText, relativePath);
        return new Parser(tokens, relativePath).ParseFile();
    }

    // ---- tokenizer -------------------------------------------------------

    internal enum Tt { Word, Var, CmdSub, String, DQString, Op, Eof }

    internal sealed record Token(Tt Type, string Text, int Line, bool Interp = false);

    private static readonly HashSet<char> s_specials = new()
    { ' ', '\t', '\r', '\n', ';', '|', '&', '(', ')', '{', '}', '<', '>', '\'', '"', '`', '$', '#' };

    private static List<Token> Tokenize(string src, string path)
    {
        var toks = new List<Token>();
        int i = 0, line = 1;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (c is ' ' or '\t' or '\r') { i++; continue; }
            if (c == '#' && (i == 0 || " \t\r\n;|&(){}><'\"".Contains(src[i - 1])))
            {
                // '#…' starts a comment only at a word boundary (shebang lines included)
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (c == '\'')
            {
                int startLine = line;
                i++;
                var sb = new StringBuilder();
                while (i < src.Length && src[i] != '\'')
                {
                    if (src[i] == '\n') throw new ParseHardError($"{path}:{startLine}: unterminated string literal");
                    sb.Append(src[i]); i++;
                }
                if (i >= src.Length) throw new ParseHardError($"{path}:{startLine}: unterminated string literal");
                i++;
                toks.Add(new Token(Tt.String, sb.ToString(), startLine));
                continue;
            }
            if (c == '"')
            {
                int startLine = line;
                i++;
                var sb = new StringBuilder();
                bool interp = false;
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\n') throw new ParseHardError($"{path}:{startLine}: unterminated string literal");
                    if (src[i] == '$' && i + 1 < src.Length && (char.IsLetter(src[i + 1]) || src[i + 1] == '{' || src[i + 1] == '('))
                        interp = true;
                    sb.Append(src[i]); i++;
                }
                if (i >= src.Length) throw new ParseHardError($"{path}:{startLine}: unterminated string literal");
                i++;
                toks.Add(new Token(Tt.DQString, sb.ToString(), startLine, interp));
                continue;
            }
            if (c == '`')
            {
                int startLine = line;
                i++;
                while (i < src.Length && src[i] != '`')
                {
                    if (src[i] == '\n') throw new ParseHardError($"{path}:{startLine}: unterminated backtick substitution");
                    line += src[i] == '\n' ? 1 : 0;
                    i++;
                }
                if (i >= src.Length) throw new ParseHardError($"{path}:{startLine}: unterminated backtick substitution");
                line++;
                i++;
                toks.Add(new Token(Tt.CmdSub, "$(cmd)", startLine));
                continue;
            }
            if (c == '$' && i + 1 < src.Length && src[i + 1] == '(')
            {
                int startLine = line;
                i += 2;
                int depth = 1;
                while (i < src.Length && depth > 0)
                {
                    if (src[i] == '\n') line++;
                    else if (src[i] == '(') depth++;
                    else if (src[i] == ')') depth--;
                    i++;
                }
                if (depth != 0) throw new ParseHardError($"{path}:{startLine}: unterminated command substitution");
                toks.Add(new Token(Tt.CmdSub, "$(cmd)", startLine));
                continue;
            }
            if (c == '$')
            {
                int startLine = line;
                int start = i + 1;
                i++;
                var sb = new StringBuilder("$");
                if (i < src.Length && src[i] == '{')
                {
                    int close = src.IndexOf('}', i);
                    if (close < 0) throw new ParseHardError($"{path}:{startLine}: unterminated variable");
                    sb.Append('{').Append(src[(i + 1)..close]);
                    i = close + 1;
                }
                else
                {
                    while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                    sb.Append(src[start..i]);
                }
                toks.Add(new Token(Tt.Var, sb.ToString(), startLine));
                continue;
            }
            // (( arithmetic )) — scanned opaquely, no tokens ($((…)) is already
            // consumed by the '$(' branch above)
            if (c == '(' && i + 1 < src.Length && src[i + 1] == '(')
            {
                int startLine = line;
                i += 2;
                int depth = 2;
                while (i < src.Length && depth > 0)
                {
                    if (src[i] == '\n') line++;
                    else if (src[i] == '(') depth++;
                    else if (src[i] == ')') depth--;
                    i++;
                }
                if (depth != 0) throw new ParseHardError($"{path}:{startLine}: unterminated arithmetic group");
                continue;
            }
            // '2' as a standalone fd digit at a word boundary: 2> / 2>> are file
            // redirects (stderr → normalized to the plain truncate/append ops,
            // same catalog rules); 2>&… fd-merges emit no event
            if (c == '2' && i + 1 < src.Length && src[i + 1] == '>'
                && (i == 0 || s_specials.Contains(src[i - 1])))
            {
                if (i + 2 < src.Length && src[i + 2] == '&')
                {
                    toks.Add(new(Tt.Op, ">&", line));
                    i += 3; // fd-merge (e.g. 2>&1): no open event
                    continue;
                }
                bool append = i + 2 < src.Length && src[i + 2] == '>';
                toks.Add(new(Tt.Op, append ? ">>" : ">", line));
                i += append ? 3 : 2;
                continue;
            }
            if (c == '<')
            {
                if (i + 2 < src.Length && src[i + 2] == '<') // <<< here-string
                { toks.Add(new(Tt.Op, "<<<", line)); i += 3; continue; }
                if (i + 1 < src.Length && src[i + 1] == '<') // << heredoc / <<- tab-stripped heredoc
                {
                    int startLine = line;
                    int j = i + 2;
                    if (j < src.Length && src[j] == '-') j++;
                    int ds = j;
                    while (j < src.Length && !s_specials.Contains(src[j]) && src[j] != '\n') j++;
                    if (j == ds)
                    { toks.Add(new(Tt.Op, "<<", line)); i += 2; continue; }
                    string delim = src[ds..j];
                    i = j;
                    while (i < src.Length && src[i] != '\n') i++; // rest of the opener line
                    if (i < src.Length) { i++; line++; }
                    bool closed = false;
                    while (i < src.Length)
                    {
                        int le = i;
                        while (le < src.Length && src[le] != '\n') le++;
                        if (src[i..le].TrimEnd('\r').TrimStart() == delim)
                        { closed = true; i = le < src.Length ? le + 1 : le; line++; break; }
                        if (le >= src.Length) { i = le; break; }
                        i = le + 1;
                        line++;
                    }
                    if (!closed) throw new ParseHardError($"{path}:{startLine}: unterminated heredoc");
                    toks.Add(new(Tt.Op, "<<", startLine)); // stdin from literal text: no open event
                    continue;
                }
                if (i + 1 < src.Length && src[i + 1] == '>') // <> read-write open: no catalog rule → no event
                { toks.Add(new(Tt.Op, "<>", line)); i += 2; continue; }
                toks.Add(new(Tt.Op, "<", line));
                i++;
                continue;
            }
            if (c == '>')
            {
                if (i + 1 < src.Length && src[i + 1] == '>') // >> append redirect
                { toks.Add(new(Tt.Op, ">>", line)); i += 2; continue; }
                if (i + 1 < src.Length && src[i + 1] == '|') // >| clobber: no catalog rule → no event
                { toks.Add(new(Tt.Op, ">|", line)); i += 2; continue; }
                if (i + 1 < src.Length && src[i + 1] == '&') // >& fd-merge: no open event
                { toks.Add(new(Tt.Op, ">&", line)); i += 2; continue; }
                toks.Add(new(Tt.Op, ">", line));
                i++;
                continue;
            }
            if (c == '&')
            {
                if (i + 1 < src.Length && src[i + 1] == '&') // && list
                { toks.Add(new(Tt.Op, "&&", line)); i += 2; continue; }
                if (i + 1 < src.Length && src[i + 1] == '>') // &> / &>> both-streams file redirect → plain open
                {
                    bool append = i + 2 < src.Length && src[i + 2] == '>';
                    toks.Add(new(Tt.Op, append ? ">>" : ">", line));
                    i += append ? 3 : 2;
                    continue;
                }
                toks.Add(new(Tt.Op, "&", line));
                i++;
                continue;
            }
            if (c == '|')
            {
                if (i + 1 < src.Length && src[i + 1] == '|') // || list
                { toks.Add(new(Tt.Op, "||", line)); i += 2; continue; }
                if (i + 1 < src.Length && src[i + 1] == '&') // |& pipe both streams
                { toks.Add(new(Tt.Op, "|&", line)); i += 2; continue; }
                toks.Add(new(Tt.Op, "|", line));
                i++;
                continue;
            }
            if (c == ';')
            {
                if (i + 2 < src.Length && src[i + 1] == ';' && src[i + 2] == '&') // ;;& case terminator
                { toks.Add(new(Tt.Op, ";;&", line)); i += 3; continue; }
                if (i + 1 < src.Length && src[i + 1] == ';') // ;; case terminator
                { toks.Add(new(Tt.Op, ";;", line)); i += 2; continue; }
                if (i + 1 < src.Length && src[i + 1] == '&') // ;& case fallthrough
                { toks.Add(new(Tt.Op, ";&", line)); i += 2; continue; }
                toks.Add(new(Tt.Op, ";", line));
                i++;
                continue;
            }
            if (c is '(' or ')' or '{' or '}')
            { toks.Add(new(Tt.Op, c.ToString(), line)); i++; continue; }
            // bare word: any run without specials ('-' '.' '/' ':' '=' allowed inside)
            int s = i;
            while (i < src.Length && !s_specials.Contains(src[i])) i++;
            if (i == s) { i++; continue; }
            toks.Add(new Token(Tt.Word, src[s..i], line));
        }
        toks.Add(new Token(Tt.Eof, "", line));
        return toks;
    }

    // ---- structural pass (TDS 5.4 Phase B) -------------------------------

    /// <summary>Statements attributed to one method body (function body, @test body, or the script scope).</summary>
    internal sealed class Scope
    {
        public List<InvocationDecl> Invocations { get; } = new();
    }

    private sealed class Parser
    {
        private readonly List<Token> _toks;
        private readonly string _path;
        private readonly List<MethodDecl> _methods = new();

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
            return new CodeModel(_path, new[] { new TypeDecl(PseudoTypeName(), null, _methods) }, null);
        }

        private void ScanScope(List<Token> toks, Scope scope)
        {
            int i = 0;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Eof) break;
                if (t.Type == Tt.Op && (t.Text == "}" || t.Text == ")")) break; // enclosing group closer

                if (t.Type == Tt.Word && IsCommandCandidate(toks, i))
                {
                    switch (t.Text)
                    {
                        case "function":
                            if (ParseFunctionKeyword(toks, ref i, scope)) continue;
                            break;
                        case "if":
                        case "elif":
                            ParseIf(toks, ref i, scope);
                            continue;
                        case "else":
                        {
                            i++;
                            var elseBody = CollectUntilWord(toks, ref i, "fi", ("if", "fi"));
                            ScanScope(elseBody, scope);
                            if (i < toks.Count && toks[i].Type == Tt.Word && toks[i].Text == "fi") i++;
                            continue;
                        }
                        case "while":
                        case "until":
                            ParseWhile(toks, ref i, scope);
                            continue;
                        case "for":
                        case "select":
                            ParseFor(toks, ref i, scope);
                            continue;
                        case "case":
                            if (ParseCase(toks, ref i, scope)) continue;
                            break;
                        case "[[":
                            SkipUntilWord(toks, ref i, "]]");
                            continue;
                        case "[":
                            SkipUntilWord(toks, ref i, "]");
                            continue;
                    }
                }

                // `name() { … }` — function definition (recognized recursively at any depth)
                if (t.Type == Tt.Word
                    && i + 3 < toks.Count
                    && AtOp(toks, i + 1, "(")
                    && AtOp(toks, i + 2, ")")
                    && AtOp(toks, i + 3, "{")
                    && !t.Text.Contains('='))
                {
                    var body = CollectBalanced(toks, ref i, '{', '}', start: i + 3);
                    _methods.Add(new MethodDecl(t.Text, PseudoTypeName(), null, t.Line,
                        Array.Empty<string>(), Array.Empty<AttributeDecl>(), ScanBody(body)));
                    continue;
                }

                // Bats: @test "title" { … } → sh.entry-attributed method ("@test" is one word token)
                if (t.Type == Tt.Word && t.Text == "@test"
                    && i + 2 < toks.Count && toks[i + 1].Type is Tt.String or Tt.DQString
                    && AtOp(toks, i + 2, "{"))
                {
                    var title = toks[i + 1].Text;
                    int line = t.Line;
                    int k = i + 2;
                    var body = CollectBalanced(toks, ref k, '{', '}');
                    bool skip = body.Any(b => b.Type == Tt.Word && b.Text == "skip");
                    var attrs = new List<AttributeDecl>
                    {
                        new("sh.entry", new List<AttributeArg>
                        {
                            new(ArgKind.Literal, title, "title"),
                            new(ArgKind.Literal, title, "full"),
                            new(ArgKind.Literal, skip ? "skip" : "run", "state")
                        })
                    };
                    _methods.Add(new MethodDecl(title + "@" + line, PseudoTypeName(), null, line,
                        Array.Empty<string>(), attrs, StatementScanner.Scan(body)));
                    i = k;
                    continue;
                }

                // brace group / subshell at a statement start: contents attribute to the same scope
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
                if (t.Type == Tt.Op && t.Text is ";" or ";;" or ";&" or ";;&" or "|&") // stray separators
                { i++; continue; }

                // assignment at command position is not a command
                if (t.Type == Tt.Word && t.Text.Contains('=')) { i++; continue; }

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
            if (p.Type == Tt.Op && p.Text is ";" or "|" or "&&" or "||" or "(" or "{" or "}" or ";;" or ";&" or ";;&" or "|&") return true;
            return p.Line < t.Line || p.Type is Tt.String or Tt.DQString or Tt.Var or Tt.CmdSub;
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
                    if (a.Text is ";;" or ";&" or ";;&" or "|" or "&&" or "||" or "|&"
                        or "(" or ")" or "{" or "}" or "<") break;
                }
                if (a.Type == Tt.Word && stmt[^1].Line < a.Line) break; // new command word on a later line
                if (a.Type == Tt.Word && a.Text.Contains('=')) break; // inline env assignment terminates the command
                stmt.Add(a);
                i++;
            }
            return stmt;
        }

        private static bool AtOp(List<Token> toks, int i, string text) =>
            i < toks.Count && toks[i].Type == Tt.Op && toks[i].Text == text;

        private static List<Token> CollectBalanced(List<Token> toks, ref int i, char open, char close, int start = -1)
        {
            if (start >= 0) i = start;
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

        /// <summary>Scans a function body slice into its own scope and returns the lowered invocations.
        /// Runs on this parser instance so nested function definitions found inside the body are
        /// added to the shared method list (TDS 5.4 Phase B).</summary>
        private List<InvocationDecl> ScanBody(List<Token> body)
        {
            var child = new Scope();
            ScanScopePrivate(body, child);
            return child.Invocations;
        }

        private void ScanScopePrivate(List<Token> toks, Scope scope) => ScanScope(toks, scope);

        /// <summary>Collects tokens until an end word (nesting-aware: open/close word pairs are tracked at any depth).</summary>
        private static List<Token> CollectUntilWord(List<Token> toks, ref int i,
            string endWord, params (string Open, string Close)[] nest)
        {
            var collected = new List<Token>();
            int[] depth = new int[nest.Length];
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Eof) break;
                bool isEnd = t.Type == Tt.Word && t.Text == endWord && Array.TrueForAll(depth, d => d == 0);
                if (isEnd) break;
                for (int n = 0; n < nest.Length; n++)
                {
                    if (t.Type == Tt.Word && t.Text == nest[n].Open) depth[n]++;
                    else if (t.Type == Tt.Word && t.Text == nest[n].Close && depth[n] > 0) depth[n]--;
                }
                collected.Add(t);
                i++;
            }
            return collected;
        }

        /// <summary>Skips tokens until an end word; hard error when the construct is unterminated.</summary>
        private static void SkipUntilWord(List<Token> toks, ref int i, string endWord)
        {
            int start = i;
            var skipped = CollectUntilWord(toks, ref i, endWord);
            if (i >= toks.Count || toks[i].Type != Tt.Word || toks[i].Text != endWord)
                throw new ParseHardError($"unterminated '{toks[start].Text}'");
            i++; // consume the end word
        }

        /// <summary>if/elif COND; then BODY … — condition and body attribute to the same scope; elif/else/fi chains are consumed here.</summary>
        private void ParseIf(List<Token> toks, ref int i, Scope scope)
        {
            i++; // if / elif
            var cond = CollectUntilWord(toks, ref i, "then", ("if", "fi"));
            ScanScope(cond, scope);
            while (i < toks.Count && toks[i].Type == Tt.Word && toks[i].Text == "then")
            {
                i++;
                var body = CollectIfBody(toks, ref i, out string? stop);
                ScanScope(body, scope);
                if (stop is null) break;
                if (stop == "fi") { i++; break; }
                if (stop == "elif")
                {
                    i++; // elif → same shape as if
                    var cond2 = CollectUntilWord(toks, ref i, "then", ("if", "fi"));
                    ScanScope(cond2, scope);
                    continue;
                }
                if (stop == "else")
                {
                    i++;
                    var elseBody = CollectUntilWord(toks, ref i, "fi", ("if", "fi"));
                    ScanScope(elseBody, scope);
                    if (i < toks.Count && toks[i].Type == Tt.Word && toks[i].Text == "fi") i++;
                    break;
                }
            }
        }

        /// <summary>Collects an if body up to its fi / elif / else boundary (nesting-aware for inner if…fi).</summary>
        private static List<Token> CollectIfBody(List<Token> toks, ref int i, out string? stop)
        {
            var collected = new List<Token>();
            int depth = 0;
            stop = null;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Eof) break;
                if (t.Type == Tt.Word && depth == 0 && (t.Text == "fi" || t.Text == "elif" || t.Text == "else"))
                { stop = t.Text; break; }
                if (t.Type == Tt.Word && t.Text == "if") depth++;
                else if (t.Type == Tt.Word && t.Text == "fi" && depth > 0) depth--;
                collected.Add(t);
                i++;
            }
            return collected;
        }

        /// <summary>while/until COND; do BODY; done — condition and body attribute to the same scope.</summary>
        private void ParseWhile(List<Token> toks, ref int i, Scope scope)
        {
            i++;
            var cond = CollectUntilWord(toks, ref i, "do", ("while", "done"), ("until", "done"));
            ScanScope(cond, scope);
            if (i < toks.Count && toks[i].Type == Tt.Word && toks[i].Text == "do") i++;
            var body = CollectUntilWord(toks, ref i, "done", ("while", "done"), ("until", "done"), ("for", "done"), ("select", "done"));
            ScanScope(body, scope);
            if (i < toks.Count && toks[i].Type == Tt.Word && toks[i].Text == "done") i++;
        }

        /// <summary>for/select NAME [in WORDS]; do BODY; done — the word list is literal, body attributes to the same scope.</summary>
        private void ParseFor(List<Token> toks, ref int i, Scope scope)
        {
            i++;
            var skipped = CollectUntilWord(toks, ref i, "do", ("for", "done"), ("select", "done"));
            if (i >= toks.Count || toks[i].Type != Tt.Word || toks[i].Text != "do")
                ScanScope(skipped, scope); // no `do` found: tolerate, scan what we saw
            if (i < toks.Count && toks[i].Type == Tt.Word && toks[i].Text == "do") i++;
            var body = CollectUntilWord(toks, ref i, "done", ("for", "done"), ("select", "done"), ("while", "done"), ("until", "done"));
            ScanScope(body, scope);
            if (i < toks.Count && toks[i].Type == Tt.Word && toks[i].Text == "done") i++;
        }

        /// <summary>case WORD in PATTERN) BODY ;; … esac — case bodies attribute to the same scope.</summary>
        private bool ParseCase(List<Token> toks, ref int i, Scope scope)
        {
            int start = i;
            i++;
            CollectUntilWord(toks, ref i, "in", ("case", "esac")); // the case word (literal for these purposes)
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Eof) break;
                if (t.Type == Tt.Word && t.Text == "esac") { i++; return true; }
                if (t.Type == Tt.Op && t.Text == ")")
                {
                    i++;
                    var body = CollectCaseBody(toks, ref i, out bool sawEsac);
                    ScanScope(body, scope);
                    if (sawEsac) return true;
                    continue;
                }
                i++; // pattern tokens (and their `|` separators)
            }
            i = start;
            return false; // malformed case → generic handling
        }

        /// <summary>Collects a case-clause body up to its ;; / ;& / ;;& / esac boundary (nesting-aware for inner case…esac).</summary>
        private static List<Token> CollectCaseBody(List<Token> toks, ref int i, out bool sawEsac)
        {
            var collected = new List<Token>();
            int depth = 0;
            sawEsac = false;
            while (i < toks.Count)
            {
                var t = toks[i];
                if (t.Type == Tt.Eof) break;
                if (t.Type == Tt.Op && depth == 0 && (t.Text == ";;" || t.Text == ";&" || t.Text == ";;&"))
                { i++; break; }
                if (t.Type == Tt.Word && t.Text == "case") depth++;
                else if (t.Type == Tt.Word && t.Text == "esac" && depth > 0) depth--;
                else if (t.Type == Tt.Word && t.Text == "esac" && depth == 0)
                { sawEsac = true; break; }
                collected.Add(t);
                i++;
            }
            return collected;
        }

        /// <summary>function name [()] { … } — the body becomes a MethodDecl.</summary>
        private bool ParseFunctionKeyword(List<Token> toks, ref int i, Scope scope)
        {
            int start = i;
            i++;
            if (i >= toks.Count || toks[i].Type != Tt.Word || toks[i].Text.Contains('='))
            { i = start; return false; }
            string name = toks[i].Text;
            int line = toks[i].Line;
            i++;
            if (AtOp(toks, i, "(") && AtOp(toks, i + 1, ")")) i += 2;
            if (!AtOp(toks, i, "{"))
            {
                // body via a compound command: scan the remainder into the current scope
                i = start;
                return false;
            }
            var body = CollectBalanced(toks, ref i, '{', '}');
            _methods.Add(new MethodDecl(name, PseudoTypeName(), null, line,
                Array.Empty<string>(), Array.Empty<AttributeDecl>(), ScanBody(body)));
            return true;
        }
    }

    /// <summary>
    /// The fixed call-shape scanner (TDS 5.4 Phase A, retained): a bare word
    /// at a statement start is a command invocation (`git status` → method
    /// "git", args ["status"]); `&gt;` / `&gt;&gt;` redirections after a
    /// command synthesize open invocations; variable and `$(…)` arguments
    /// carry their argument kinds.
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
                if (t.Type != Tt.Word) { i++; continue; }
                bool commandPosition =
                    i == 0
                    || (toks[i - 1].Type == Tt.Op && toks[i - 1].Text is ";" or "|" or "&&" or "||" or "(" or "{" or "}" or ";;" or ";&" or ";;&" or "|&")
                    || toks[i - 1].Line < t.Line
                    || toks[i - 1].Type is Tt.String or Tt.DQString or Tt.Var or Tt.CmdSub;
                if (!commandPosition) { i++; continue; }
                if (t.Text.Contains('=')) { i++; continue; } // assignment words are not commands

                int j = i + 1;
                var args = new List<CallArg>();
                while (j < toks.Count)
                {
                    var a = toks[j];
                    if (a.Type == Tt.Op && (a.Text == ";" || a.Text == "|" || a.Text == "&&" || a.Text == "||"
                        || a.Text == "(" || a.Text == "{" || a.Text == "}" || a.Text == "<")) break;
                    if (a.Type == Tt.Op && (a.Text == ">" || a.Text == ">>")) break;
                    if (a.Type == Tt.Word && j > 0 && toks[j - 1].Line < a.Line) break;
                    if (a.Type == Tt.Word)
                    {
                        if (a.Text.Contains('=')) break; // inline env assignment terminates the command
                        args.Add(CallArg.Literal(a.Text));
                        j++;
                        continue;
                    }
                    if (a.Type == Tt.String) { args.Add(CallArg.Literal(a.Text)); j++; continue; }
                    if (a.Type == Tt.DQString)
                    { args.Add(a.Interp ? new CallArg(ArgKind.Interpolated, a.Text) : CallArg.Literal(a.Text)); j++; continue; }
                    if (a.Type == Tt.Var) { args.Add(new CallArg(ArgKind.Identifier, a.Text)); j++; continue; }
                    if (a.Type == Tt.CmdSub) { args.Add(new CallArg(ArgKind.Expression, "")); j++; continue; }
                    break;
                }
                invocations.Add(new InvocationDecl(null, t.Text, t.Line, args));
                i = j;
                // `> path` / `>> path` after the command → open invocation
                if (i < toks.Count && toks[i].Type == Tt.Op && (toks[i].Text == ">" || toks[i].Text == ">>"))
                {
                    string op = toks[i].Text;
                    int line = toks[i].Line;
                    i++;
                    if (i < toks.Count && toks[i].Type is Tt.Word or Tt.String or Tt.DQString)
                    {
                        var tk = toks[i];
                        CallArg target = tk.Type switch
                        {
                            Tt.String => CallArg.Literal(tk.Text),
                            Tt.DQString => tk.Interp
                                ? new CallArg(ArgKind.Interpolated, tk.Text)
                                : CallArg.Literal(tk.Text),
                            _ => CallArg.Literal(tk.Text) // bare words are literal paths
                        };
                        i++;
                        invocations.Add(new InvocationDecl(null, op, line, new[] { target }));
                    }
                }
            }
            return invocations;
        }
    }
}
