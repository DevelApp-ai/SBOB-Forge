using System.Text;
using SBOBForge.Core;

namespace SBOBForge.Parsing;

/// <summary>
/// Deterministic PowerShell lexical parser (TDS 5.4 Phase A): a fixed token
/// scanner recognizing the catalog's call shapes — Pester v5 `Describe`/
/// `Context`/`It [-Skip]` blocks, `function` blocks for SUT attribution,
/// command invocations with `-Param value` named arguments, the call
/// operator `&amp;`, `New-Object TypeName(…)` / `[Type]::new(…)` construction,
/// and `&gt;`/`&gt;&gt;` redirections. Not a full grammar: statements outside
/// modeled blocks land in a `&lt;top-level&gt;` helper method (harness noise),
/// and param blocks / expressions are tolerated but not modeled. Pinned as
/// lock grammar "powershell" (backend "sbobforge-powershell"); every
/// downstream artifact marks files from this backend `parser: lexical`
/// (TDS 5.4). Unterminated strings or unbalanced braces are hard errors
/// (TDS 13).
/// </summary>
public sealed class PowerShellSourceParser : ICodeModelParser
{
    public const string BackendId = "sbobforge-powershell";
    public const string Version = "1.0.0";

    string ICodeModelParser.BackendId => BackendId;

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

    // ---- lexical structural pass ------------------------------------------

    private sealed class Parser
    {
        private readonly List<Token> _toks;
        private readonly string _path;

        public Parser(List<Token> toks, string path) { _toks = toks; _path = path; }

        private Token Cur => _toks[_pos];
        private bool IsOp(string text) => Cur.Type == Tt.Op && Cur.Text == text;
        private bool IsWord(string text) => Cur.Type == Tt.Ident && Cur.Text == text;
        private Token Take() => _toks[_pos++];
        private int _pos;

        public CodeModel ParseFile()
        {
            var methods = new List<MethodDecl>();
            var leftover = new List<Token>();
            Walk(_toks, new List<string>(), methods, leftover);

            // statements outside modeled blocks: harness/setup surface → helper noise rows
            var leftoverInvocations = StatementScanner.Scan(leftover);
            if (leftoverInvocations.Count > 0)
                methods.Add(new MethodDecl("<top-level>", PseudoTypeName(), null, 0,
                    Array.Empty<string>(), Array.Empty<AttributeDecl>(), leftoverInvocations));

            return new CodeModel(_path, new[] { new TypeDecl(PseudoTypeName(), null, methods) }, null);
        }

        private string PseudoTypeName() =>
            System.IO.Path.GetFileNameWithoutExtension(_path).Replace('.', '-');

        /// <summary>Walks tokens: functions, Describe/Context scopes, It entries; everything else pools to `leftover`.</summary>
        private void Walk(List<Token> toks, List<string> describeStack,
            List<MethodDecl> methods, List<Token> leftover)
        {
            int i = 0;
            while (i < toks.Count)
            {
                var t = toks[i];
                // function Name { … } / function Name (params) { … }
                if (t.Type == Tt.Ident && t.Text == "function")
                {
                    int j = i + 1;
                    if (j < toks.Count && toks[j].Type == Tt.Ident)
                    {
                        string name = toks[j].Text;
                        int line = toks[j].Line;
                        int k = j + 1;
                        if (k < toks.Count && toks[k].Type == Tt.Op && toks[k].Text == "(")
                        {
                            int depth = 0;
                            k++;
                            while (k < toks.Count)
                            {
                                if (toks[k].Type == Tt.Op && toks[k].Text == "(") depth++;
                                else if (toks[k].Type == Tt.Op && toks[k].Text == ")") { if (depth-- == 0) { k++; break; } }
                                k++;
                            }
                        }
                        if (k < toks.Count && toks[k].Type == Tt.Op && toks[k].Text == "{")
                        {
                            var body = CollectBalanced(toks, ref k, '{', '}');
                            methods.Add(new MethodDecl(name, PseudoTypeName(), null, line,
                                Array.Empty<string>(), Array.Empty<AttributeDecl>(),
                                StatementScanner.Scan(body)));
                            i = k;
                            continue;
                        }
                    }
                    leftover.Add(t);
                    i++;
                    continue;
                }
                // Describe "title" { … } / Context "title" { … }
                if (t.Type == Tt.Ident && (t.Text == "Describe" || t.Text == "Context")
                    && i + 1 < toks.Count && toks[i + 1].Type is Tt.String or Tt.DQString)
                {
                    var title = toks[i + 1].Text;
                    int k = i + 2;
                    if (k < toks.Count && toks[k].Type == Tt.Op && toks[k].Text == "{")
                    {
                        describeStack.Add(title);
                        var inner = CollectBalanced(toks, ref k, '{', '}');
                        Walk(inner, describeStack, methods, leftover);
                        describeStack.RemoveAt(describeStack.Count - 1);
                        i = k;
                        continue;
                    }
                }
                // It "title" [-Skip] { … } → ps.entry-attributed method
                if (t.Type == Tt.Ident && t.Text == "It"
                    && i + 1 < toks.Count && toks[i + 1].Type is Tt.String or Tt.DQString)
                {
                    var title = toks[i + 1].Text;
                    int line = toks[i].Line;
                    int k = i + 2;
                    bool skip = false;
                    while (k < toks.Count
                           && !(toks[k].Type == Tt.Op && toks[k].Text == "{")
                           && toks[k].Type != Tt.Eof)
                    {
                        if (toks[k].Type == Tt.Param && toks[k].Text == "-Skip") skip = true;
                        k++;
                    }
                    if (k < toks.Count && toks[k].Type == Tt.Op && toks[k].Text == "{")
                    {
                        var body = CollectBalanced(toks, ref k, '{', '}');
                        var full = describeStack.Count > 0
                            ? string.Join(" > ", describeStack) + " > " + title
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
            throw new ParseHardError("unbalanced '{'");
        }
    }

    /// <summary>
    /// The fixed call-shape scanner (TDS 5.4 Phase A): recognizes command
    /// words at statement starts with positional / `-Param value` named
    /// arguments, the `&amp;` call operator, `New-Object Type(…)` and
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
