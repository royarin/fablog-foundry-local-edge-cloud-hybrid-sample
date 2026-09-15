using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using Azure.AI.OpenAI;
using FabLog.Core;
using Microsoft.Extensions.AI;

namespace FabLog.FabPad;

/// <summary>
/// T3 from the device's point of view: <b>its own wire to the vendor</b>.
/// <para>
/// Nothing brokers this. The pad does not reach Azure through TrustHub, because
/// a service in front of the cloud is still the device's way out — and if the
/// only wire out goes through the hub then <c>CloudOnly</c> is not a posture the
/// device can hold, it is a posture the hub holds on its behalf. The ledger can
/// say whatever it likes; the transport decides what is true.
/// </para>
/// <para>
/// It is also where offline is <i>discovered</i>. There is no probe and no health
/// poll. Every call either completes — any status, 200 or 401 or 429,
/// all of which mean the wire is there — or throws at the transport, and this
/// records which. <see cref="IConnectivity"/> reports that observation to the
/// badge. Nothing consults it beforehand.
/// </para>
/// </summary>
public sealed class CloudClient : IChatClient, IConnectivity
{
    readonly IChatClient _inner;

    public CloudClient(T3Options options, IAirlock switches)
    {
        Configured = options.IsConfigured;

        // 🚪 The cleanroom boundary goes here — on the transport, underneath the
        // SDK, so everything above it is the real path. This is one of exactly TWO
        // places FabPad registers the handler; HubClient is the other, because the
        // cleanroom blocks the fab's own LAN too. The on-device model is absent from
        // that list: it has no transport to wrap.
        var http = new HttpClient(new AirlockHandler(switches) { InnerHandler = new HttpClientHandler() })
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(options.TimeoutSeconds, 1)),
        };

        _inner = !Configured
            ? new NotConfiguredChatClient()
            : new AzureOpenAIClient(
                    options.ResourceEndpoint,
                    new ApiKeyCredential(options.ApiKey),
                    new AzureOpenAIClientOptions
                    {
                        Transport = new HttpClientPipelineTransport(http),
                        NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                    })
                .GetChatClient(options.Deployment)
                .AsIChatClient();
    }

    /// <summary>False when <c>FabLog:T3</c> still holds template placeholders — see <see cref="NotConfiguredChatClient"/>.</summary>
    public bool Configured { get; }

    // ── IConnectivity: an observation of the last attempt, never a prediction ─
    public bool CloudReachable { get; private set; } = true;
    public DateTimeOffset? UnreachableSince { get; private set; }

    public event Action? ReachabilityChanged;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            var response = await _inner.GetResponseAsync(messages, options, ct);
            Observe(reachable: true);
            return response;
        }
        catch (Exception ex)
        {
            // A refusal is not a wall. 401, 404, 429 all mean the wire is fine and
            // something else is wrong — reporting "airgapped" because a key expired
            // would put a false statement on the connectivity badge.
            Observe(reachable: !IsTransportFailure(ex));
            throw;
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException("FabPad does not stream — see FoundryChatClient.");

    /// <summary>Fire-and-forget polish against the vendor, straight from the device. No hub in the path.</summary>
    public async Task<bool> TrySendAsync(string payload, CancellationToken ct = default)
    {
        try
        {
            await GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.Polish), new ChatMessage(ChatRole.User, payload)],
                Prompts.ProseOptions, ct);
            return true;
        }
        catch { return false; }
    }

    void Observe(bool reachable)
    {
        if (reachable == CloudReachable) return;
        CloudReachable = reachable;
        UnreachableSince = reachable ? null : DateTimeOffset.Now;
        ReachabilityChanged?.Invoke();
    }

    /// <summary>
    /// Did the request fail to reach anything at all? Walk the whole inner-exception
    /// chain — the SDK wraps, and a <c>SocketException</c> three levels down is still
    /// a dead wire.
    /// </summary>
    static bool IsTransportFailure(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is System.Net.Sockets.SocketException or HttpRequestException or TimeoutException)
                return true;
        return false;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
