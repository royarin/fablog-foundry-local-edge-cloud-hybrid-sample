using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using FabLog.Core;
using Microsoft.Extensions.AI;

namespace FabLog.TrustHub;

// ⚠️ T3Options and NotConfiguredChatClient live in FabLog.Core, not here. Two
// processes bind the same shape, because the device reaches the vendor over its
// own wire and the hub is not the only holder of a cloud credential. Keeping two
// copies of "is this configured?" is how one of them quietly drifts into saying
// yes.

// ⚠️ There is deliberately no hub-side airlock, and its absence is the
// architecture.
//
// Mirroring FabPad's cleanroom toggle over loopback — one click cutting both
// uplinks — would only make sense if the boundary were drawn around the whole
// building. It isn't: the boundary is the cleanroom door, this server is on the
// other side of it, and it never leaves the network.
//
// It also could not work. The moment the door seals, the pad cannot reach the hub
// to tell it anything — so the mirror would fail silently and the hub would keep
// its (now correct) internet by accident rather than by design.

public static class Cloud
{
    /// <summary>
    /// The Central System's own wire to the vendor — one of <b>two</b> in the
    /// system, not the only one, and it never goes down: this server is outside the
    /// cleanroom and stays on the network.
    /// <para>
    /// What it sends is categorically different from what the device sends: a
    /// <see cref="HubStore.AsDigest"/>, never a note body. The Central System holds
    /// every technician's full text because it is in the fab's own jurisdiction;
    /// that is precisely why nothing it holds may leave verbatim.
    /// </para>
    /// </summary>
    public static IChatClient Create(T3Options o) =>
        !o.IsConfigured
            ? new NotConfiguredChatClient()
            : new AzureOpenAIClient(
                    o.ResourceEndpoint,
                    new ApiKeyCredential(o.ApiKey),
                    new AzureOpenAIClientOptions
                    {
                        Transport = new HttpClientPipelineTransport(
                            new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(o.TimeoutSeconds, 1)) }),
                        NetworkTimeout = TimeSpan.FromSeconds(o.TimeoutSeconds),
                    })
                .GetChatClient(o.Deployment)
                .AsIChatClient();
}
