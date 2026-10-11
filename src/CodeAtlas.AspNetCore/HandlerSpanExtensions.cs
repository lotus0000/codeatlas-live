using CodeAtlas.AspNetCore.Spans;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAtlas.AspNetCore;

public static class HandlerSpanExtensions
{
    /// <summary>
    /// Opt-in Minimal API handler span. Placed after an endpoint's own filters (e.g. after <c>WithValidation</c>) it covers
    /// the handler only; on a group it is the outermost filter and also covers the endpoints' filters.
    /// Wrap the call in <c>#if CODEATLAS</c>.
    /// </summary>
    public static TBuilder WithCodeAtlasHandlerSpan<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilterFactory((factory, next) =>
        {
            var method = factory.MethodInfo;
            return async context =>
            {
                if (context.HttpContext.RequestServices.GetService<CodeAtlasRuntime>() is not { WrapperSpansEnabled: true })
                    return await next(context);
                using var activity = CodeAtlasDiagnostics.Source.StartActivity($"handler {method.DeclaringType?.Name}.{method.Name}");
                activity?.SetTag("codeatlas.component", "handler");
                MethodIdentity.Tag(activity, method, Evidence.Executed);
                try
                {
                    return await next(context);
                }
                catch (Exception e)
                {
                    CodeAtlasDiagnostics.Fail(activity, e);
                    throw;
                }
            };
        });
}
