using FabLog.TrustHub;
using Microsoft.Extensions.Configuration;

namespace FabLog.DemoTests;

/// <summary>
/// The hub's own configuration, read once, from the same two files the hub reads.
/// </summary>
public static class LiveConfig
{
    public static readonly T3Options T3 = Load();

    /// <summary>True only when a real endpoint, deployment and key are all present.</summary>
    public static bool Available => T3.IsConfigured;

    public const string WhySkipped =
        "T3 is not configured, so the live cloud tests are skipped. To run them, create "
        + "src/FabLog.TrustHub/appsettings.Development.json (gitignored) with real "
        + "Endpoint / Deployment / ApiKey values — see SETUP.md.";

    static T3Options Load() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build()
            .GetSection("FabLog:T3").Get<T3Options>() ?? new T3Options();
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself when there is no cloud credential.
/// <para>
/// The alternative — a <c>Category=Live</c> filter you have to remember to pass —
/// fails in both directions: the suite is red on a machine with no config, and
/// the live tests silently never run on the machine that does have it. A test
/// that decides for itself does neither. The placeholder-fidelity check in
/// particular is one you want running <i>automatically</i> the moment a real
/// endpoint appears, because it is the one that can break masking invisibly.
/// </para>
/// <para>
/// The <c>[Trait("Category", "Live")]</c> on the class is kept so these can still
/// be selected or excluded explicitly when something needs isolating.
/// </para>
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (!LiveConfig.Available) Skip = LiveConfig.WhySkipped;
    }
}
