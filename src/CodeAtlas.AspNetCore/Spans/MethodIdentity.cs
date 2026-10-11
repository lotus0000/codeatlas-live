using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace CodeAtlas.AspNetCore.Spans;

internal enum Evidence { Executed, Routing }

/// <summary>MVID + metadata token identify the exact method (overloads, lambdas) for PDB lookup in the collector.</summary>
internal static class MethodIdentity
{
    public static void Tag(Activity? activity, MethodInfo? method, Evidence evidence)
    {
        if (activity is null || method?.DeclaringType is null) return;
        activity.SetTag("codeatlas.code.function", $"{method.DeclaringType.FullName}.{method.Name}");
        activity.SetTag("codeatlas.code.mvid", method.Module.ModuleVersionId.ToString());
        activity.SetTag("codeatlas.code.token", method.MetadataToken);
        activity.SetTag("codeatlas.code.assembly_path", method.Module.Assembly.Location);
        activity.SetTag("codeatlas.code.evidence", evidence == Evidence.Executed ? "executed" : "routing");
    }

    public static MethodInfo? HandlerOf(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.MethodInfo ?? endpoint.Metadata.GetMetadata<MethodInfo>();
}
