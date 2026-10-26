using Microsoft.AspNetCore.Routing.Patterns;

namespace BanteraApi.Jobs;

public sealed record OperationHandler(RequestDelegate Execute, bool Streaming, bool Admin, TimeSpan Budget);
public sealed class OperationRegistry
{
    private readonly Dictionary<string, OperationHandler> handlers = new();
    public void Add(string kind, OperationHandler handler) => handlers.Add(kind, handler);
    public OperationHandler Get(string kind) => handlers[kind];
}

public static class OperationRoutes
{
    // Keep the original binding/validation and response contracts; only the execution lifetime changes.
    public static RouteHandlerBuilder MapAiOperationPost(this IEndpointRouteBuilder routes, string pattern,
        string kind, Delegate handler, bool streaming = false, bool admin = false, int minutes = 10)
    {
        var executor = RequestDelegateFactory.Create(handler, new RequestDelegateFactoryOptions {
            ServiceProvider = routes.ServiceProvider,
            RouteParameterNames = RoutePatternFactory.Parse(pattern).Parameters.Select(p => p.Name),
            ThrowOnBadRequest = true
        }).RequestDelegate;
        routes.ServiceProvider.GetRequiredService<OperationRegistry>().Add(kind,
            new(executor, streaming, admin, TimeSpan.FromMinutes(minutes)));
        return routes.MapPost(pattern, (HttpContext context, OperationService service) => service.AcceptAsync(context, kind));
    }
}
