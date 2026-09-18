namespace SoR.Gateway.Transport;

/// <summary>
/// Copies the caller's <c>Authorization</c> header, unchanged, onto every outgoing subgraph request
/// (docs/version-facts.md §5). The subgraphs validate the token and enforce tenant and service access;
/// the gateway never interprets it beyond the edge check.
/// </summary>
public sealed class ForwardAuthorizationHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var auth = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth) && request.Headers.Authorization is null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
