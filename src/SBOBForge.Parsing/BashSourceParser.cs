using System.Text;
using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// Deterministic Bash lexical parser (TDS 5.4 Phase A): a fixed token scanner
/// recognizing the catalog's call shapes — Bats `@test "title" { … }` entries
/// with the `skip` disable cue, `name() { … }` / `function name { … }` blocks
/// for SUT attribution, bare command words with literal/variable arguments,
/// and `&gt;` / `&gt;&gt;` redirection opens. Not a full grammar: shell
/// control flow is tolerated, statements outside modeled blocks land in a
/// `&lt;top-level&gt;` helper method, and `$(…)` / backtick substitutions are
/// opaque expressions. Pinned as lock grammar "bash" (backend
/// "sbobforge-bash"); every downstream artifact marks files from this
/// backend `parser: lexical` (TDS 5.4). Unterminated strings or unbalanced
/// braces are hard errors (TDS 13).
/// </summary>
public sealed class BashSourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-bash";
    public const string Version = "1.0.0";

    string ICodeModelParser.BackendId => BackendId;

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
                    i++;
                }
                if (i >= src.Length) throw new ParseHardError($"{path}:{startLine}: unterminated backtick substitution");
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
            if (c == '>' && i + 1 < src.Length && src[i + 1] == '>')
            { toks.Add(new(Tt.Op, ">>", line)); i += 2; continue; }
            if (c is '>' or '<' or ';' or '|' or '&' or '(' or ')' or '{' or '}')
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

    // ---- lexical structural pass ------------------------------------------

    private sealed class Parser
    {
        private readonly List<Token> _toks;
        private readonly string _path;

        public Parser(List<Token> toks, string path) { _toks = toks; _path = path; }

        public CodeModel ParseFile()
        {
            var methods = new List<MethodDecl>();
            var leftover = new List<Token>();
            Walk(methods, leftover);

            var leftoverInvocations = StatementScanner.Scan(leftover);
            if (leftoverInvocations.Count > 0)
                methods.Add(new MethodDecl("<top-level>", PseudoTypeName(), null, 0,
                    Array.Empty<string>(), Array.Empty<AttributeDecl>(), leftoverInvocations));

            return new CodeModel(_path, new[] { new TypeDecl(PseudoTypeName(), null, methods) }, null);
        }

        private string PseudoTypeName() =>
            System.IO.Path.GetFileNameWithoutExtension(_path).Replace('.', '-');

        private void Walk(List<MethodDecl> methods, List<Token> leftover)
        {
            int i = 0;
            while (i < _toks.Count)
            {
                var t = _toks[i];

                // `function name { … }`
                if (t.Type == Tt.Word && t.Text == "function"
                    && i + 1 < _toks.Count && _toks[i + 1].Type == Tt.Word)
                {
                    int line = t.Line;
                    string name = _toks[i + 1].Text;
                    int k = i + 2;
                    if (k < _toks.Count && _toks[k].Type == Tt.Op && _toks[k].Text == "("
                        && k + 1 < _toks.Count && _toks[k + 1].Type == Tt.Op && _toks[k + 1].Text == ")")
                        k += 2;
                    if (k < _toks.Count && _toks[k].Type == Tt.Op && _toks[k].Text == "{")
                    {
                        var body = CollectBalanced(ref k);
                        methods.Add(new MethodDecl(name, PseudoTypeName(), null, line,
                            Array.Empty<string>(), Array.Empty<AttributeDecl>(), StatementScanner.Scan(body)));
                        i = k;
                        continue;
                    }
                }

                // `name() { … }`
                if (t.Type == Tt.Word
                    && i + 2 < _toks.Count
                    && _toks[i + 1].Type == Tt.Op && _toks[i + 1].Text == "("
                    && _toks[i + 2].Type == Tt.Op && _toks[i + 2].Text == ")")
                {
                    int k = i + 3;
                    if (k < _toks.Count && _toks[k].Type == Tt.Op && _toks[k].Text == "{")
                    {
                        var body = CollectBalanced(ref k);
                        methods.Add(new MethodDecl(t.Text, PseudoTypeName(), null, t.Line,
                            Array.Empty<string>(), Array.Empty<AttributeDecl>(), StatementScanner.Scan(body)));
                        i = k;
                        continue;
                    }
                }

                // Bats: @test "title" { … } → sh.entry-attributed method ("@test" is one word token)
                if (t.Type == Tt.Word && t.Text == "@test"
                    && i + 1 < _toks.Count && _toks[i + 1].Type is Tt.String or Tt.DQString)
                {
                    var title = _toks[i + 1].Text;
                    int line = t.Line;
                    int k = i + 2;
                    if (k < _toks.Count && _toks[k].Type == Tt.Op && _toks[k].Text == "{")
                    {
                        var body = CollectBalanced(ref k);
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
                        methods.Add(new MethodDecl(title + "@" + line, PseudoTypeName(), null, line,
                            Array.Empty<string>(), attrs, StatementScanner.Scan(body)));
                        i = k;
                        continue;
                    }
                }

                leftover.Add(t);
                i++;
            }
        }

        private List<Token> CollectBalanced(ref int i)
        {
            var inner = new List<Token>();
            i++; // '{'
            int depth = 1;
            while (i < _toks.Count)
            {
                var t = _toks[i];
                if (t.Type == Tt.Op && t.Text == "{") { depth++; inner.Add(t); i++; continue; }
                if (t.Type == Tt.Op && t.Text == "}")
                {
                    depth--;
                    if (depth == 0) { i++; return inner; }
                    inner.Add(t); i++; continue;
                }
                inner.Add(t);
                i++;
            }
            throw new ParseHardError($"{_path}: unbalanced '{{'");
        }
    }

    /// <summary>
    /// The fixed call-shape scanner (TDS 5.4 Phase A): a bare word at a
    /// statement start is a command invocation (`git status` → method "git",
    /// args ["status"]); `&gt;` / `&gt;&gt;` redirections after a command
    /// synthesize open invocations; variable and `$(…)` arguments carry the
    /// reduced-confidence argument kinds.
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
                    || (toks[i - 1].Type == Tt.Op && toks[i - 1].Text is ";" or "|" or "&&" or "||" or "(" or "{" or "}")
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
