using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CodeAtlas.AspNetCore.Spans;

/// <summary>Zero-change MVC action span. Registered with the highest order, so it is the innermost filter around the action method.</summary>
internal sealed class ActionSpanFilter(CodeAtlasRuntime runtime) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!runtime.WrapperSpansEnabled)
        {
            await next();
            return;
        }
        var method = (context.ActionDescriptor as ControllerActionDescriptor)?.MethodInfo;
        using var activity = CodeAtlasDiagnostics.Source.StartActivity($"action {method?.DeclaringType?.Name}.{method?.Name}");
        activity?.SetTag("codeatlas.component", "action");
        MethodIdentity.Tag(activity, method, Evidence.Executed);
        var executed = await next();
        if (executed.Exception is { } exception && !executed.ExceptionHandled) CodeAtlasDiagnostics.Fail(activity, exception);
    }
}
