using System.Runtime.CompilerServices;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;

namespace CodeAtlas.AspNetCore.Spans;

/// <summary>
/// Zero-change endpoint span for Minimal APIs and controllers: the matched endpoint is swapped for a copy (same pattern,
/// order and metadata) whose delegate is wrapped. The span covers binding, filters, handler and result writing; its
/// method identity is routing metadata, because a filter may stop the request before the handler runs.
/// </summary>
internal sealed class EndpointSpanPolicy(CodeAtlasRuntime runtime) : MatcherPolicy, IEndpointSelectorPolicy
{
    private readonly ConditionalWeakTable<RouteEndpoint, RouteEndpoint> _wrapped = new();

    public override int Order => int.MaxValue;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) => true;

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        if (!runtime.WrapperSpansEnabled) return Task.CompletedTask;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i) || candidates[i].Endpoint is not RouteEndpoint { RequestDelegate: not null } endpoint) continue;
            candidates.ReplaceEndpoint(i, _wrapped.GetValue(endpoint, Wrap), candidates[i].Values);
        }
        return Task.CompletedTask;
    }

    private static RouteEndpoint Wrap(RouteEndpoint endpoint)
    {
        var inner = endpoint.RequestDelegate!;
        var handler = MethodIdentity.HandlerOf(endpoint);
        return new RouteEndpoint(async context =>
        {
            using var activity = CodeAtlasDiagnostics.Source.StartActivity($"endpoint {endpoint.DisplayName}");
            activity?.SetTag("codeatlas.component", "endpoint");
            MethodIdentity.Tag(activity, handler, Evidence.Routing);
            try
            {
                await inner(context);
            }
            catch (Exception e)
            {
                CodeAtlasDiagnostics.Fail(activity, e);
                throw;
            }
        }, endpoint.RoutePattern, endpoint.Order, endpoint.Metadata, endpoint.DisplayName);
    }
}
