using System.Net;
using System.Net.Http;

namespace StoreBridge.Apple.Tests;

/// <summary>
/// Fake HTTP handler for unit-testing Apple verifiers without real network calls.
/// Returns responses in order; repeats the last one if more calls are made than responses provided.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly (HttpStatusCode Status, string Body)[] _responses;
    private int _index;

    public int CallCount => _index;

    public FakeHttpMessageHandler(params (HttpStatusCode Status, string Body)[] responses)
        => _responses = responses;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var i = Math.Min(_index++, _responses.Length - 1);
        var (status, body) = _responses[i];
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        });
    }
}
