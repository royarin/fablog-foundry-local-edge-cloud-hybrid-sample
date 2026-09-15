using Microsoft.Extensions.AI;

namespace FabLog.Core;

/// <summary>
/// Binds <c>FabLog:T3</c>. Real values live only in the gitignored
/// <c>appsettings.Development.json</c> beside each process.
/// <para>
/// ⚠️ <b>Both processes bind this, not just the hub.</b> FabPad reaches the vendor
/// directly, so the device holds a cloud credential of its own — it has to, because
/// <c>CloudOnly</c> is a posture the device must be able to hold by itself.
/// </para>
/// <para>
/// Lives in Core rather than in either app so that "which cloud" is one shape
/// of configuration, bound the same way on both sides of the sync. Note there
/// is no SDK type in this file: Core stays dependency-free, and each app's
/// composition root builds its own client from these values.
/// </para>
/// </summary>
public sealed class T3Options
{
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// True only when all three values are present <i>and</i> none is still a
    /// template placeholder. The committed template ships <c>"&lt;…&gt;"</c> values on
    /// purpose, so "is this configured?" has to mean more than "is it non-empty".
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !Endpoint.StartsWith('<')
        && !string.IsNullOrWhiteSpace(Deployment) && !Deployment.StartsWith('<')
        && !string.IsNullOrWhiteSpace(ApiKey) && !ApiKey.StartsWith('<');

    /// <summary>
    /// The endpoint the Azure OpenAI client actually wants: the resource root,
    /// with no path on it.
    /// <para>
    /// The Foundry portal shows a <i>project</i> endpoint far more prominently than
    /// the data-plane one — <c>https://{resource}.services.ai.azure.com/api/projects/{project}</c>
    /// — and that is what you will paste if you are in a hurry. It is a real
    /// endpoint for a different SDK (Azure AI Projects / Agents). Give it to the
    /// Azure OpenAI client and every call comes back <b>HTTP 400 "API version not
    /// supported"</b>, which sends you hunting for an api-version problem you do
    /// not have.
    /// </para>
    /// <para>So: keep the scheme and host, drop the path. Both spellings of the host
    /// (<c>services.ai.azure.com</c> and <c>openai.azure.com</c>) work — verified
    /// against a live resource, every api-version from 2024-10-21 on.</para>
    /// <para>
    /// <b>This is the knob that moves the jurisdiction.</b> A different region, a
    /// different vendor, a sovereign deployment — same <c>IChatClient</c>, same call
    /// sites, same binary.
    /// </para>
    /// </summary>
    public Uri ResourceEndpoint => new UriBuilder(Endpoint) { Path = "", Query = "", Fragment = "" }.Uri;
}

/// <summary>
/// What T3 is until a real endpoint is dropped in. It <b>fails loudly</b> rather
/// than returning plausible text: a stub that quietly answers would hide the
/// difference between "the cloud tier works" and "the cloud tier is merely wired",
/// which is precisely the difference you need to see.
/// <para>
/// Lives in Core because <b>both</b> processes need it — and because two copies of
/// "fail loudly" is exactly the kind of duplication that ends with one copy quietly
/// learning to succeed.
/// </para>
/// <para>Tests never see this — they inject deterministic fakes.</para>
/// </summary>
public sealed class NotConfiguredChatClient : IChatClient
{
    public const string Message =
        "T3 (VendorAI) is not configured. Set FabLog:T3:Endpoint, :Deployment and :ApiKey " +
        "in appsettings.Development.json — the committed appsettings.json holds placeholders only.";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new InvalidOperationException(Message);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new InvalidOperationException(Message);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
