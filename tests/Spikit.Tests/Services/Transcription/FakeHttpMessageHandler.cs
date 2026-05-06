using System.Net;

namespace Spikit.Tests.Services.Transcription;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> CapturedBodies { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public static FakeHttpMessageHandler Returning(HttpStatusCode status, string body, string contentType = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
        }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            CapturedBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }
        return await _handler(request, cancellationToken);
    }
}
