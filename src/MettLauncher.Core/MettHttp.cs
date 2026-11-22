using System.Net.Http.Headers;
using System.Reflection;

namespace MettLauncher.Core;

/// <summary>Erzeugt den gemeinsamen HttpClient des Launchers (User-Agent ist für die GitHub-API Pflicht).</summary>
public static class MettHttp
{
    public static string UserAgentVersion =>
        typeof(MettHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null
            ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
            : new HttpClient(handler, disposeHandler: false);
        Configure(client);
        return client;
    }

    public static void Configure(HttpClient client)
    {
        client.Timeout = Timeout.InfiniteTimeSpan; // Downloads können lange dauern; Abbruch läuft über CancellationToken.
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MettLauncher", UserAgentVersion));
    }
}
