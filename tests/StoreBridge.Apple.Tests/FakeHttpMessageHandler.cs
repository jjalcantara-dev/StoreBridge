using System.Net;
using System.Net.Http;

namespace StoreBridge.Apple.Tests;

/// <summary>
/// Fake HTTP handler for unit-testing Apple verifiers without real network calls.
/// Returns responses in order; repeats the last one if more calls are made than responses provided.
/// A response with a non-null <c>Throw</c> makes that call throw instead of answering.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly (HttpStatusCode Status, string Body, Exception? Throw)[] _responses;
    private int _index;

    public int CallCount => _index;

    /// <summary>Absolute URIs of every request, in order.</summary>
    public List<Uri> RequestUris { get; } = [];

    /// <summary>Bearer token of the last request.</summary>
    public string? LastBearerToken { get; private set; }

    public FakeHttpMessageHandler(params (HttpStatusCode Status, string Body)[] responses)
        => _responses = responses.Select(r => (r.Status, r.Body, (Exception?)null)).ToArray();

    public FakeHttpMessageHandler(params (HttpStatusCode Status, string Body, Exception? Throw)[] responses)
        => _responses = responses;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUris.Add(request.RequestUri!);
        LastBearerToken = request.Headers.Authorization?.Parameter;

        var i = Math.Min(_index++, _responses.Length - 1);
        var (status, body, toThrow) = _responses[i];
        if (toThrow != null)
            throw toThrow;

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        });
    }
}
