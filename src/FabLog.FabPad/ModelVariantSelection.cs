namespace FabLog.FabPad;

internal sealed record ModelVariantInfo(string Id, string? ExecutionProvider);

internal static class ModelVariantSelection
{
    public static IReadOnlyList<ModelVariantInfo> SelectCandidates(
        IEnumerable<ModelVariantInfo> variants,
        IEnumerable<string> registeredProviders,
        string? forcedVariant = null)
    {
        var available = new HashSet<string>(registeredProviders, StringComparer.OrdinalIgnoreCase)
        {
            // CPU is built in, rather than a downloadable WinML provider.
            "CPUExecutionProvider",
        };

        if (!string.IsNullOrWhiteSpace(forcedVariant))
        {
            var forced = variants.FirstOrDefault(v =>
                v.Id.Contains(forcedVariant.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No model variant matches ForceVariant '{forcedVariant}'.");
            if (forced.ExecutionProvider is not { Length: > 0 } ep || !available.Contains(ep))
                throw new InvalidOperationException(
                    $"Forced variant '{forced.Id}' requires '{forced.ExecutionProvider ?? "(unknown)"}', " +
                    "which is not registered. No fallback will be attempted.");
            return [forced];
        }

        return [.. variants.Where(v =>
            v.ExecutionProvider is { Length: > 0 } ep && available.Contains(ep))];
    }
}
