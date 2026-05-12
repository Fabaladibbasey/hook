using Microsoft.AspNetCore.HttpOverrides;

namespace Hook.Shared.Core;

internal static class ForwardedHeadersConfigurator
{
    public const string ConfigSection = "ForwardedHeaders:KnownNetworks";

    /// <summary>
    /// Production stack is Caddy → api over a private docker bridge; trust scope is the
    /// bridge CIDR from <c>ForwardedHeaders:KnownNetworks</c> (defaults to 172.16.0.0/12).
    /// Defaults (loopback) are cleared explicitly since collection-initializer syntax only
    /// calls Add. <c>ForwardLimit=1</c> pins single-hop trust so a chain of forged
    /// X-Forwarded-For entries from a real bridge client cannot smuggle an external IP.
    /// </summary>
    public static ForwardedHeadersOptions Build(IConfiguration configuration)
    {
        var opts = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        opts.KnownIPNetworks.Clear();
        opts.KnownProxies.Clear();

        foreach (var cidr in configuration.GetSection(ConfigSection).Get<string[]>() ?? [])
        {
            if (System.Net.IPNetwork.TryParse(cidr, out var network))
            {
                opts.KnownIPNetworks.Add(network);
            }
        }

        return opts;
    }
}
