using System.Diagnostics;
using System.Net.Sockets;

namespace FabLog.Core;

public static class VendorErrors
{
    public static string Describe(Exception error)
    {
        Trace.TraceError("Vendor call failed: {0}", error);

        IReadOnlyList<Exception> failures = error is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [error];
        var attempts = failures.Count > 1 ? $" after {failures.Count} attempts" : "";

        if (failures.SelectMany(Causes).Any(e =>
                e is InvalidOperationException && e.Message == NotConfiguredChatClient.Message))
            return NotConfiguredChatClient.Message;

        if (failures.All(e => Causes(e).Any(c => c is TimeoutException or OperationCanceledException)))
            return $"Vendor request timed out or was canceled{attempts}. Try again.";

        if (failures.All(e => Causes(e).Any(c =>
                c is SocketException or HttpRequestException { StatusCode: null }
                    or TimeoutException or OperationCanceledException)))
            return $"Vendor unreachable{attempts}. Check the connection and try again.";

        return "Vendor request failed. Check the service configuration and try again.";
    }

    static IEnumerable<Exception> Causes(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
            yield return error;
    }
}
