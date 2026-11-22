using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace MettLauncher.Core.Tests.Support;

/// <summary>Gefakter HttpMessageHandler: Antworten pro URL (ohne Query) registrieren, Requests mitschneiden.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _routes = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Wenn gesetzt, wirft jeder Request (simuliert „offline“).</summary>
    public bool Offline { get; set; }

    public FakeHttpHandler On(string url, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        On(url, (req, _) => Task.FromResult(respond(req)));

    public FakeHttpHandler On(string url, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _routes[Key(new Uri(url))] = respond;
        return this;
    }

    public FakeHttpHandler OnJson(string url, string json, string? etag = null) =>
        On(url, _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (etag is not null) r.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
            return r;
        });

    public FakeHttpHandler OnBytes(string url, byte[] bytes) =>
        On(url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        if (Offline) throw new HttpRequestException("offline (fake)");
        if (_routes.TryGetValue(Key(request.RequestUri!), out var respond))
            return await respond(request, ct);
        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
    }

    private static string Key(Uri uri) => uri.GetLeftPart(UriPartial.Path);
}
