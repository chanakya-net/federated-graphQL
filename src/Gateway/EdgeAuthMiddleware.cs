namespace SoR.Gateway;

/// <summary>
/// Edge authentication (contracts/http-and-env.md): a request to <c>/graphql</c> that could execute an
/// operation (any POST, or a GET carrying a <c>query</c>) without a valid token gets <c>401</c> with an empty
/// body and never reaches a subgraph. The Nitro UI (<c>GET /graphql/</c> and its assets) loads without a token.
/// Signature and expiry only; tenant and service claims are the subgraphs' business.
/// </summary>
public sealed class EdgeAuthMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/graphql")
            && CanExecuteOperation(context.Request)
            && context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool CanExecuteOperation(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        || request.Query.ContainsKey("query");
}
