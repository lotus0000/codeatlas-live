using System.Diagnostics;
using CodeAtlas.AspNetCore.Export;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAtlas.AspNetCore.Http;

/// <summary>First middleware in the pipeline: notes method, route and status for the request's Activity, without tagging it.</summary>
internal sealed class HttpFactsStartupFilter(HttpFactsStore store) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var activity = Activity.Current;
            var failed = false;
            try
            {
                await nextMiddleware(context);
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                if (activity?.Source.Name == SpanSerializer.AspNetCoreSource)
                    store.Set(activity, new HttpFacts(
                        context.Request.Method,
                        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
                        failed ? 500 : context.Response.StatusCode,
                        failed));
            }
        });
        next(app);
    };
}
