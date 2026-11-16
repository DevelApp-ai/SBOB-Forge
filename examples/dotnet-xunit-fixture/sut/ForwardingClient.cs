using System.IO;
using System.Net.Http;

namespace Example.Core.Sut;

/// <summary>
/// The forwarding shape (SBOB-Forge #49): every egress candidate in the package is
/// dynamically addressed — the request URL is an identifier, not a constant — and
/// the temp location is an identifier too. Honest packages do this on purpose
/// (an HTTP-forwarding plugin is supposed to connect to request-derived
/// destinations); the declared netPolicies/pathPolicies entries lift them to
/// tier-P wildcard entries instead of failing the whole package into quarantine.
/// </summary>
public sealed class ForwardingClient
{
    public string Forward(string targetUrl)
    {
        using var http = new HttpClient();
        return http.GetStringAsync(targetUrl).Result;
    }

    public void SaveSnapshot(string tempRoot, string snapshot)
    {
        File.WriteAllText(tempRoot, snapshot);
    }
}
