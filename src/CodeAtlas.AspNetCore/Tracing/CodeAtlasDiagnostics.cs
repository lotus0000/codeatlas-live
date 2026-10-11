using System.Diagnostics;

namespace CodeAtlas.AspNetCore.Tracing;

internal static class CodeAtlasDiagnostics
{
    public const string SourceName = "CodeAtlas.Components";

    public static readonly ActivitySource Source = new(SourceName, "0.1.0");

    public static void Fail(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity?.AddException(exception);
    }
}
