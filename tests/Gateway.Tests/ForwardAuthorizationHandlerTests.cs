using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using SoR.Gateway.Transport;

namespace SoR.Gateway.Tests;

public sealed class ForwardAuthorizationHandlerTests
{
    [Fact]
    public async Task ForwardAuthorizationHandler_copies_header()
    {
        var sent = await SendAsync(incomingAuthorization: "Bearer x", outgoing: new HttpRequestMessage(HttpMethod.Post, "http://patch/graphql"));

        Assert.Equal("Bearer x", sent.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task ForwardAuthorizationHandler_does_not_overwrite_an_existing_header()
    {
        var outgoing = new HttpRequestMessage(HttpMethod.Post, "http://patch/graphql");
        outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "already-set");

        var sent = await SendAsync(incomingAuthorization: "Bearer x", outgoing);

        Assert.Equal("Bearer already-set", sent.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task ForwardAuthorizationHandler_no_header_no_copy()
    {
        var sent = await SendAsync(incomingAuthorization: null, outgoing: new HttpRequestMessage(HttpMethod.Post, "http://patch/graphql"));

        Assert.Null(sent.Headers.Authorization);
        Assert.False(sent.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task ForwardAuthorizationHandler_without_http_context_sends_unchanged()
    {
        var capture = new CapturingHandler();
        using var handler = new ForwardAuthorizationHandler(new HttpContextAccessor()) { InnerHandler = capture };
        using var invoker = new HttpMessageInvoker(handler);

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://patch/graphql"), CancellationToken.None);

        Assert.Null(capture.Request!.Headers.Authorization);
    }

    private static async Task<HttpRequestMessage> SendAsync(string? incomingAuthorization, HttpRequestMessage outgoing)
    {
        var context = new DefaultHttpContext();
        if (incomingAuthorization is not null) context.Request.Headers.Authorization = incomingAuthorization;

        var capture = new CapturingHandler();
        using var handler = new ForwardAuthorizationHandler(new HttpContextAccessor { HttpContext = context }) { InnerHandler = capture };
        using var invoker = new HttpMessageInvoker(handler);
        using var response = await invoker.SendAsync(outgoing, CancellationToken.None);
        return capture.Request!;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
