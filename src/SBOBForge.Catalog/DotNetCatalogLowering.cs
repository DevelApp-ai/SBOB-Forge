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
/// The .NET lowering engine (TDS 7.3): matches invocations against the
/// embedded catalog and resolves argument values to the extend possible.
/// Quarantine — never best-effort guessing — for anything not statically
/// resolvable.
/// </summary>
public static class DotNetCatalogLowering
{
    private const string ProtocolTcp = "TCP";

    public static DotNetCatalog Catalog { get; } = DotNetCatalog.LoadDefault();

    /// <summary>
    /// Lower one invocation. Returns null when no catalog rule matches.
    /// <paramref name="substitute"/> lets the caller resolve identifier
    /// arguments (M1: theory-case values bound by parameter name).
    /// </summary>
    public static LoweredInvocation? Lower(
        InvocationDecl invocation,
        Func<CallArg, CallArg?>? substitute = null)
    {
        CatalogRule? rule = null;
        foreach (var r in Catalog.Rules)
        {
            if (!string.Equals(r.Method, invocation.Method, StringComparison.Ordinal)) continue;
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
            "unrepresentable" => LowerUnrepresentable(rule, invocation),
            "exec" => LowerExec(rule, invocation, substitute),
            "egress" => LowerEgress(rule, invocation, substitute),
            "ingress" => LowerIngress(rule, invocation, substitute),
            _ => throw new InvalidOperationException($"Unknown catalog rule kind '{rule.Kind}' ({rule.Id})")
        };
    }

    private static CallArg Resolve(CallArg arg, Func<CallArg, CallArg?>? substitute) =>
        substitute?.Invoke(arg) ?? arg;

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
            resolved.Value, rule.Flags, Array.Empty<string>(), true,
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

    private static LoweredInvocation LowerUnrepresentable(CatalogRule rule, InvocationDecl inv) => new(
        rule.Id, LiftOutcome.Unrepresentable, BehaviorPrimitive.Open,
        null, Array.Empty<string>(), Array.Empty<string>(), true,
        null, null, "", NetDirection.Egress, null, rule.UnrepresentablePrimitive);

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

    private static LoweredInvocation LowerIngress(CatalogRule rule, InvocationDecl inv, Func<CallArg, CallArg?>? substitute)
    {
        // new TcpListener(IPAddress.Any, port): the port is the last literal numeric argument.
        for (var i = inv.Args.Count - 1; i >= 0; i--)
        {
            var a = Resolve(inv.Args[i], substitute);
            if (!a.IsLiteral) continue;
            if (int.TryParse(a.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                return new LoweredInvocation(
                    rule.Id, LiftOutcome.Event, BehaviorPrimitive.Listen,
                    null, Array.Empty<string>(), Array.Empty<string>(), true,
                    "", port, ProtocolTcp, NetDirection.Ingress, null, null);
        }
        return Quarantined(rule, BehaviorPrimitive.Listen, inv, "listener port is not a constant literal");
    }

    private static LoweredInvocation Quarantined(CatalogRule rule, BehaviorPrimitive primitive, InvocationDecl inv, string reason) => new(
        rule.Id, LiftOutcome.Quarantine, primitive,
        null, Array.Empty<string>(), Array.Empty<string>(), false,
        null, null, "", NetDirection.Egress, reason, null);

    private static CallArg? Arg(InvocationDecl inv, int index) =>
        inv.Args.Count > index ? inv.Args[index] : null;

    private static string KindName(CallArg arg) => arg.Kind switch
    {
        ArgKind.Interpolated => "an interpolated string",
        ArgKind.Identifier => "an identifier",
        _ => "a non-constant expression"
    };
}
