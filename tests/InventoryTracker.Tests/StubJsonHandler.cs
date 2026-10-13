using System.Net;
using System.Text;

namespace InventoryTracker.Tests;

/// <summary>Answers every request with the next of a fixed list of JSON bodies, and records the URLs asked for.</summary>
internal sealed class StubJsonHandler(params string[] bodies) : HttpMessageHandler
{
    private int _next;

    public List<string> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requested.Add(request.RequestUri!.ToString());
        var body = bodies[Math.Min(_next++, bodies.Length - 1)];
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
