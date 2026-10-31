using System;
using System.Collections.Generic;
using System.Globalization;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;

namespace SBOBForge.Catalog;

public enum LiftOutcome
{
    /// <summary>Event candidate (tier L or P assigned downstream).</summary>
    Event,

    /// <summary>Catalog-matched but not statically resolvable (tier Q, TDS 7.3).</summary>
    Quarantine,

    /// <summary>Spec v0.0.4 cannot represent it (TDS 15: delete, rename, ...).</summary>
    Unrepresentable
}

/// <summary>
/// One lowered invocation (TDS 7.3): primitive + resolved values + provisional
/// tier. The pipeline assigns TestId attribution and rewrites tier L→P through
/// path policies.
/// </summary>
public sealed record LoweredInvocation(
    string CatalogRuleId,
    LiftOutcome Outcome,
    BehaviorPrimitive Primitive,
    string? Path,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> Args,
    bool ArgsAllLiteral,
    string? DnsName,
    int? Port,
    string Protocol,
    NetDirection Direction,
    string? Reason,
    string? UnrepresentablePrimitive);

/// <summary>
/// Shared lowering engine (TDS 7.3): matches invocations against a language
/// catalog and resolves argument values where statically possible. Quarantine —
/// never best-effort guessing — for anything not statically resolvable.
/// Kinds: open, openFileMode, openModeString, openAppendable, unrepresentable,
/// exec, execTokens, execShell, subprocessExec, egress, egressHostPort,
/// ingress, ingressHostPort.
/// </summary>
public static class CatalogLowering
{
    private const string ProtocolTcp = "TCP";
    private const string Shell = "/bin/sh";

    /// <summary>Lower one invocation. Returns null when no catalog rule matches.</summary>
    public static LoweredInvocation? Lower(
        IReadOnlyList<CatalogRule> rules,
        InvocationDecl invocation,
        Func<CallArg, CallArg?>? substitute = null)
    {
        CatalogRule? rule = null;
        foreach (var r in rules)
        {
            if (r.Method is null || !string.Equals(r.Method, invocation.Method, StringComparison.Ordinal)) continue;
            if (r.Receiver is not null && !string.Equals(r.Receiver, invocation.Receiver, StringComparison.Ordinal))
                continue;
            rule = r;
            break;
        }
        if (rule is null) return null;

        return rule.Kind switch
        {
            "open" => LowerOpen(rule, invocation, substitute),
            "openFileMode" => LowerOpenFileMode(rule, invocation, substitute),
            "openModeString" => LowerOpenModeString(rule, invocation, substitute),
            "openAppendable" => LowerOpenAppendable(rule, invocation, substitute),
            "unrepresentable" => LowerUnrepresentable(rule, invocation),
            "exec" => LowerExec(rule, invocation, substitute),
            "execTokens" => LowerExecTokens(rule, invocation, substitute),
            "execShell" => LowerExecShell(rule, invocation, substitute),
            "subprocessExec" => LowerSubprocessExec(rule, invocation, substitute),
            "egress" => LowerEgress(rule, invocation, substitute),
            "egressHostPort" => LowerEgressHostPort(rule, invocation, substitute),
            "ingress" => LowerIngress(rule, invocation, substitute),
            "ingressHostPort" => LowerIngressHostPort(rule, invocation, substitute),
            _ => throw new InvalidOperationException($"Unknown catalog rule kind '{rule.Kind}' ({rule.Id})")
        };
    }

    private static CallArg Resolve(CallArg arg, Func<CallArg, CallArg?>? substitute) =>
        substitute?.Invoke(arg) ?? arg;

    private static CallArg? Arg(InvocationDecl inv, int index) =>
        inv.Args.Count > index ? inv.Args[index] : null;

    private static IReadOnlyList<string> FlagsOf(CatalogRule rule) => rule.Flags ?? Array.Empty<string>();

    // ---- open ------------------------------------------------------------

    private static LoweredInvocation LowerOpen(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        var path = Arg(inv, 0);
        if (path is null)
            return Quarantined(rule, BehaviorPrimitive.Open, inv, "open path argument missing");
        var resolved = Resolve(path, substitute);
        if (!resolved.IsLiteral)
            return Quarantined(rule, BehaviorPrimitive.Open, inv,
                $"open path is {KindName(resolved)}, not a constant literal");
        return new LoweredInvocation(
            rule.Id, LiftOutcome.Event, BehaviorPrimitive.Open,
            resolved.Value, FlagsOf(rule), Array.Empty<string>(), true,
            null, null, "", NetDirection.Egress, null, null);
    }

    private static LoweredInvocation LowerOpenFileMode(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        var path = Resolve(Arg(inv, 0) ?? new CallArg(ArgKind.Expression, "<missing>"), substitute);
        if (!path.IsLiteral)
            return Quarantined(rule, BehaviorPrimitive.Open, inv,
                $"open path is {KindName(path)}, not a constant literal");

        // File.Open(path, FileMode.X[, FileShare...]) — FileMode is a named enum member.
        var mode = Arg(inv, 1);
        var flags = mode is { Kind: ArgKind.Identifier } ? FileModeFlags(mode.Value) : null;
        if (flags is null)
            return Quarantined(rule, BehaviorPrimitive.Open, inv,
                mode is null
                    ? "FileMode argument missing"
                    : $"FileMode '{mode.Value}' is not a resolvable FileMode member");
        return new LoweredInvocation(
            rule.Id, LiftOutcome.Event, BehaviorPrimitive.Open,
            path.Value, flags, Array.Empty<string>(), true,
            null, null, "", NetDirection.Egress, null, null);
    }

    private static IReadOnlyList<string>? FileModeFlags(string member) => member switch
    {
        "FileMode.CreateNew" => new[] { "O_WRONLY", "O_CREAT" },
        "FileMode.Create" => new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" },
        "FileMode.Open" => new[] { "O_RDWR" },
        "FileMode.OpenOrCreate" => new[] { "O_RDWR", "O_CREAT" },
        "FileMode.Truncate" => new[] { "O_WRONLY", "O_TRUNC" },
        "FileMode.Append" => new[] { "O_WRONLY", "O_CREAT", "O_APPEND" },
        _ => null
    };

    /// <summary>Python open(path[, mode]): mode string → open flags (TDS 7.2). Binary 'b' and text 't' flags are ignored (bytes vs text is not a syscall difference).</summary>
    private static IReadOnlyList<string>? OpenModeStringFlags(string mode)
    {
        var m = mode.Replace("b", "").Replace("t", "");
        return m switch
        {
            "r" => new[] { "O_RDONLY" },
            "w" => new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" },
            "a" => new[] { "O_WRONLY", "O_CREAT", "O_APPEND" },
            "x" => new[] { "O_WRONLY", "O_CREAT" },
            "r+" => new[] { "O_RDWR" },
            "w+" => new[] { "O_RDWR", "O_CREAT", "O_TRUNC" },
            "a+" => new[] { "O_RDWR", "O_CREAT", "O_APPEND" },
            "x+" => new[] { "O_RDWR", "O_CREAT" },
            _ => m.EndsWith('+')
                ? new[] { "O_RDWR", "O_CREAT" }
                : null
        };
    }

    private static LoweredInvocation LowerOpenModeString(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        var path = Arg(inv, 0);
        if (path is null)
            return Quarantined(rule, BehaviorPrimitive.Open, inv, "open path argument missing");
        var resolvedPath = Resolve(path, substitute);
        if (!resolvedPath.IsLiteral)
            return Quarantined(rule, BehaviorPrimitive.Open, inv,
                $"open path is {KindName(resolvedPath)}, not a constant literal");

        var modeArg = Arg(inv, 1);
        string mode = "r";
        if (modeArg is not null)
        {
            var resolvedMode = Resolve(modeArg, substitute);
            if (!resolvedMode.IsLiteral)
                return Quarantined(rule, BehaviorPrimitive.Open, inv,
                    $"open mode is {KindName(resolvedMode)}, not a constant literal");
            mode = resolvedMode.Value;
        }
        var flags = OpenModeStringFlags(mode);
        if (flags is null)
            return Quarantined(rule, BehaviorPrimitive.Open, inv,
                $"open mode '{mode}' is not a resolvable open() mode string");
        return new LoweredInvocation(
            rule.Id, LiftOutcome.Event, BehaviorPrimitive.Open,
            resolvedPath.Value, flags, Array.Empty<string>(), true,
            null, null, "", NetDirection.Egress, null, null);
    }

    /// <summary>FileOutputStream/FileWriter(path[, append]): boolean second arg selects the flag set.</summary>
    private static LoweredInvocation LowerOpenAppendable(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        var path = Arg(inv, 0);
        if (path is null)
            return Quarantined(rule, BehaviorPrimitive.Open, inv, "open path argument missing");
        var resolvedPath = Resolve(path, substitute);
        if (!resolvedPath.IsLiteral)
            return Quarantined(rule, BehaviorPrimitive.Open, inv,
                $"open path is {KindName(resolvedPath)}, not a constant literal");

        bool append = Arg(inv, 1) is { Kind: ArgKind.Identifier, Value: "true" };
        var flags = append
            ? new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }
            : new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" };
        return new LoweredInvocation(
            rule.Id, LiftOutcome.Event, BehaviorPrimitive.Open,
            resolvedPath.Value, flags, Array.Empty<string>(), true,
            null, null, "", NetDirection.Egress, null, null);
    }

    // ---- unrepresentable ---------------------------------------------------

    private static LoweredInvocation LowerUnrepresentable(CatalogRule rule, InvocationDecl inv) => new(
        rule.Id, LiftOutcome.Unrepresentable, BehaviorPrimitive.Open,
        null, Array.Empty<string>(), Array.Empty<string>(), true,
        null, null, "", NetDirection.Egress, null, rule.UnrepresentablePrimitive);

    // ---- exec ------------------------------------------------------------

    private static LoweredInvocation LowerExec(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        var file = Arg(inv, 0);
        if (file is null)
            return Quarantined(rule, BehaviorPrimitive.Exec, inv, "exec target argument missing");
        var resolved = Resolve(file, substitute);
        if (!resolved.IsLiteral)
            return Quarantined(rule, BehaviorPrimitive.Exec, inv,
                $"exec target is {KindName(resolved)}, not a constant literal");

        var args = new List<string>();
        var allLiteral = true;
        for (var i = 1; i < inv.Args.Count; i++)
        {
            var a = Resolve(inv.Args[i], substitute);
            if (a.IsLiteral) args.Add(a.Value);
            else allLiteral = false;
        }
        return new LoweredInvocation(
            rule.Id, LiftOutcome.Event, BehaviorPrimitive.Exec,
            resolved.Value, Array.Empty<string>(), args, allLiteral,
            null, null, "", NetDirection.Egress, null, null);
    }

    /// <summary>Runtime.exec(cmd): single string literal split on whitespace, or a literal argv array.</summary>
    private static LoweredInvocation LowerExecTokens(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        var first = Arg(inv, 0);
        if (first is null)
            return Quarantined(rule, BehaviorPrimitive.Exec, inv, "exec command argument missing");
        var resolved = Resolve(first, substitute);

        string[] argv;
        if (resolved.Kind == ArgKind.List)
            argv = resolved.ListElements().ToArray();
        else if (resolved.IsLiteral)
            argv = resolved.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        else
            return Quarantined(rule, BehaviorPrimitive.Exec, inv,
                $"exec command is {KindName(resolved)}, not a constant literal");
        if (argv.Length == 0)
            return Quarantined(rule, BehaviorPrimitive.Exec, inv, "exec command is empty");

        return new LoweredInvocation(
            rule.Id, LiftOutcome.Event, BehaviorPrimitive.Exec,
            argv[0], Array.Empty<string>(), argv[1..], true,
            null, null, "", NetDirection.Egress, null, null);
    }

    /// <summary>os.system(cmd): fixed /bin/sh -c lowering (TDS 7.2).</summary>
    private static LoweredInvocation LowerExecShell(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        var cmd = Arg(inv, 0);
        if (cmd is null)
            return Quarantined(rule, BehaviorPrimitive.Exec, inv, "shell command argument missing");
        var resolved = Resolve(cmd, substitute);
        if (!resolved.IsLiteral)
            return Quarantined(rule, BehaviorPrimitive.Exec, inv,
                $"shell command is {KindName(resolved)}, not a constant literal");
        return new LoweredInvocation(
            rule.Id, LiftOutcome.Event, BehaviorPrimitive.Exec,
            Shell, Array.Empty<string>(), new[] { "-c", resolved.Value }, true,
            null, null, "", NetDirection.Egress, null, null);
    }

    /// <summary>subprocess.run/call/Popen: list argv, literal program string, or shell=True → /bin/sh -c.</summary>
    private static LoweredInvocation LowerSubprocessExec(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        // shell=True keyword argument → shell form
        foreach (var a in inv.Args)
        {
            if (!a.IsNamed("shell")) continue;
            var resolvedFlag = Resolve(a, substitute);
            if (resolvedFlag is { Kind: ArgKind.Identifier, Value: "True" })
            {
                var cmd = Arg(inv, 0);
                if (cmd is null)
                    return Quarantined(rule, BehaviorPrimitive.Exec, inv, "shell command argument missing");
                var resolvedCmd = Resolve(cmd, substitute);
                if (!resolvedCmd.IsLiteral)
                    return Quarantined(rule, BehaviorPrimitive.Exec, inv,
                        $"shell command is {KindName(resolvedCmd)}, not a constant literal");
                return new LoweredInvocation(
                    rule.Id, LiftOutcome.Event, BehaviorPrimitive.Exec,
                    Shell, Array.Empty<string>(), new[] { "-c", resolvedCmd.Value }, true,
                    null, null, "", NetDirection.Egress, null, null);
            }
        }

        var first = Arg(inv, 0);
        if (first is null)
            return Quarantined(rule, BehaviorPrimitive.Exec, inv, "exec argv argument missing");
        var resolvedArg = Resolve(first, substitute);

        if (resolvedArg.Kind == ArgKind.List)
        {
            var argv = resolvedArg.ListElements().ToArray();
            if (argv.Length == 0)
                return Quarantined(rule, BehaviorPrimitive.Exec, inv, "exec argv list is empty");
            return new LoweredInvocation(
                rule.Id, LiftOutcome.Event, BehaviorPrimitive.Exec,
                argv[0], Array.Empty<string>(), argv[1..], true,
                null, null, "", NetDirection.Egress, null, null);
        }
        if (resolvedArg.Kind == ArgKind.PartialList)
        {
            // argv0-anchoring (TDS 5.2): the program name is a literal; later args are dynamic.
            // Emit the anchored argv0 with the args wildcard instead of quarantining the whole exec.
            return new LoweredInvocation(
                rule.Id, LiftOutcome.Event, BehaviorPrimitive.Exec,
                resolvedArg.Value, Array.Empty<string>(), Array.Empty<string>(), false,
                null, null, "", NetDirection.Egress, null, null);
        }
        if (resolvedArg.IsLiteral)
        {
            // a bare string without shell=True is the program name (no extra argv)
            return new LoweredInvocation(
                rule.Id, LiftOutcome.Event, BehaviorPrimitive.Exec,
                resolvedArg.Value, Array.Empty<string>(), Array.Empty<string>(), true,
                null, null, "", NetDirection.Egress, null, null);
        }
        return Quarantined(rule, BehaviorPrimitive.Exec, inv,
            $"exec argv is {KindName(resolvedArg)}, not a constant literal or literal list");
    }

    // ---- network ----------------------------------------------------------

    private static LoweredInvocation LowerEgress(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        var url = Arg(inv, 0);
        if (url is null)
            return Quarantined(rule, BehaviorPrimitive.Connect, inv, "request URL argument missing");
        var resolved = Resolve(url, substitute);
        if (!resolved.IsLiteral)
            return Quarantined(rule, BehaviorPrimitive.Connect, inv,
                $"request URL is {KindName(resolved)}, not a constant literal");

        if (!Uri.TryCreate(resolved.Value, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return Quarantined(rule, BehaviorPrimitive.Connect, inv,
                $"request URL '{resolved.Value}' is not an absolute http(s) URL");

        var defaultPort = uri.Scheme == "https" ? 443 : 80;
        var port = uri.IsDefaultPort ? defaultPort : uri.Port;
        return new LoweredInvocation(
            rule.Id, LiftOutcome.Event, BehaviorPrimitive.Connect,
            null, Array.Empty<string>(), Array.Empty<string>(), true,
            uri.Host.ToLowerInvariant(), port, ProtocolTcp, NetDirection.Egress, null, null);
    }

    /// <summary>Socket(host, port) / socket.create_connection((host, port)) / HTTPConnection(host[, port]).</summary>
    private static LoweredInvocation LowerEgressHostPort(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        var first = Arg(inv, 0);
        if (first is null)
            return Quarantined(rule, BehaviorPrimitive.Connect, inv, "connect host argument missing");
        var resolvedFirst = Resolve(first, substitute);

        string? host = null;
        string? portText = null;
        if (resolvedFirst.Kind == ArgKind.List)
        {
            var elements = resolvedFirst.ListElements();
            if (elements.Count >= 1) host = elements[0];
            if (elements.Count >= 2) portText = elements[1];
        }
        else if (resolvedFirst.IsLiteral)
        {
            host = resolvedFirst.Value;
            if (Arg(inv, 1) is { } second)
            {
                var resolvedSecond = Resolve(second, substitute);
                if (!resolvedSecond.IsLiteral)
                    return Quarantined(rule, BehaviorPrimitive.Connect, inv,
                        $"connect port is {KindName(resolvedSecond)}, not a constant literal");
                portText = resolvedSecond.Value;
            }
        }
        else
        {
            return Quarantined(rule, BehaviorPrimitive.Connect, inv,
                $"connect host is {KindName(resolvedFirst)}, not a constant literal");
        }

        if (portText is null && rule.DefaultPort is int defaultPort)
            return new LoweredInvocation(
                rule.Id, LiftOutcome.Event, BehaviorPrimitive.Connect,
                null, Array.Empty<string>(), Array.Empty<string>(), true,
                host?.ToLowerInvariant(), defaultPort, ProtocolTcp, NetDirection.Egress, null, null);

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            return Quarantined(rule, BehaviorPrimitive.Connect, inv,
                portText is null
                    ? "connect port argument missing and the rule has no defaultPort"
                    : $"connect port '{portText}' is not a constant integer");
        return new LoweredInvocation(
            rule.Id, LiftOutcome.Event, BehaviorPrimitive.Connect,
            null, Array.Empty<string>(), Array.Empty<string>(), true,
            host?.ToLowerInvariant(), port, ProtocolTcp, NetDirection.Egress, null, null);
    }

    private static LoweredInvocation LowerIngress(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        // new ServerSocket(port[, ...]): the port is the last literal numeric argument.
        for (var i = inv.Args.Count - 1; i >= 0; i--)
        {
            var a = Resolve(inv.Args[i], substitute);
            if (!a.IsLiteral) continue;
            if (int.TryParse(a.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                return new LoweredInvocation(
                    rule.Id, LiftOutcome.Event, BehaviorPrimitive.Listen,
                    null, Array.Empty<string>(), Array.Empty<string>(), true,
                    "", port, ProtocolOf(rule, ProtocolTcp), NetDirection.Ingress, null, null);
        }
        return Quarantined(rule, BehaviorPrimitive.Listen, inv, "listener port is not a constant literal");
    }

    /// <summary>sock.bind((host, port)) / HTTPServer((addr, port), handler): address tuple, port literal.</summary>
    private static LoweredInvocation LowerIngressHostPort(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        var first = Arg(inv, 0);
        if (first is not null)
        {
            var resolved = Resolve(first, substitute);
            if (resolved.Kind == ArgKind.List)
            {
                var elements = resolved.ListElements();
                for (var i = elements.Count - 1; i >= 0; i--)
                {
                    if (int.TryParse(elements[i], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                        return new LoweredInvocation(
                            rule.Id, LiftOutcome.Event, BehaviorPrimitive.Listen,
                            null, Array.Empty<string>(), Array.Empty<string>(), true,
                            "", port, ProtocolOf(rule, ProtocolTcp), NetDirection.Ingress, null, null);
                }
            }
        }
        // fall back to the last literal numeric argument
        return LowerIngress(rule, inv, substitute);
    }

    private static string ProtocolOf(CatalogRule rule, string fallback) =>
        string.IsNullOrEmpty(rule.Protocol) ? fallback : rule.Protocol!;

    // ---- helpers ----------------------------------------------------------

    private static LoweredInvocation Quarantined(CatalogRule rule, BehaviorPrimitive primitive, InvocationDecl inv, string reason) => new(
        rule.Id, LiftOutcome.Quarantine, primitive,
        null, Array.Empty<string>(), Array.Empty<string>(), false,
        null, null, "", NetDirection.Egress, reason, null);

    private static string KindName(CallArg arg) => arg.Kind switch
    {
        ArgKind.Interpolated => "an interpolated string",
        ArgKind.Identifier => "an identifier",
        ArgKind.List => "a list with non-literal elements",
        _ => "a non-constant expression"
    };
}

/// <summary>.NET lowering entry point (TDS 7.3); kept as a stable API over the shared engine.</summary>
public static class DotNetCatalogLowering
{
    public static LanguageCatalog Catalog { get; } = DotNetCatalog.LoadDefault();

    public static LoweredInvocation? Lower(
        InvocationDecl invocation,
        Func<CallArg, CallArg?>? substitute = null) =>
        CatalogLowering.Lower(Catalog.Rules, invocation, substitute);
}

/// <summary>Java lowering entry point (TDS 7.3).</summary>
public static class JavaCatalogLowering
{
    public static LanguageCatalog Catalog { get; } = JavaCatalog.LoadDefault();

    public static LoweredInvocation? Lower(
        InvocationDecl invocation,
        Func<CallArg, CallArg?>? substitute = null) =>
        CatalogLowering.Lower(Catalog.Rules, invocation, substitute);
}

/// <summary>Python lowering entry point (TDS 7.3).</summary>
public static class PythonCatalogLowering
{
    public static LanguageCatalog Catalog { get; } = PythonCatalog.LoadDefault();

    public static LoweredInvocation? Lower(
        InvocationDecl invocation,
        Func<CallArg, CallArg?>? substitute = null) =>
        CatalogLowering.Lower(Catalog.Rules, invocation, substitute);
}
