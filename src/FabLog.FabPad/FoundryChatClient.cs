using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;
using Microsoft.Extensions.AI;
using BetalgoMessage = Betalgo.Ranul.OpenAI.ObjectModels.RequestModels.ChatMessage;

namespace FabLog.FabPad;

/// <summary>
/// Foundry Local's chat client, as an <see cref="IChatClient"/>.
/// <para>
/// ⚠️ There is no <c>.AsIChatClient()</c> on the SDK's client, unlike the Azure one:
/// <c>Microsoft.AI.Foundry.Local.WinML</c> 1.2.4 is built on Betalgo.Ranul.OpenAI
/// 9.1.0, and nothing in the package implements <c>IChatClient</c>. So the adapter
/// lives here, in the composition root, and <c>Tiers.cs</c> keeps its claim honestly:
/// one interface, three tiers.
/// </para>
/// <para>
/// This is the entire cost of that claim. Forty lines, in one project, written once.
/// </para>
/// </summary>
public sealed class FoundryChatClient(OpenAIChatClient chat, string modelId) : IChatClient
{
    public string ModelId => modelId;

    /// <summary>Creates the client for an already-loaded model or variant. Call <c>LoadAsync</c> first.</summary>
    public static async Task<FoundryChatClient> CreateAsync(IModel model, string modelId) =>
        new(await model.GetChatClientAsync(null), modelId);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        // Foundry Local carries generation settings on the client, not the call,
        // so per-call ChatOptions are applied here. Single-threaded by design:
        // the device runs one prompt at a time.
        chat.Settings.MaxTokens = options?.MaxOutputTokens ?? 400;
        chat.Settings.Temperature = options?.Temperature ?? 0f;
        chat.Settings.RandomSeed = (int?)options?.Seed ?? 42;

        var reply = await chat.CompleteChatAsync([.. messages.Select(Translate)], null);
        ct.ThrowIfCancellationRequested();

        if (!reply.Successful)
            throw new InvalidOperationException(reply.Error?.Message ?? "Foundry Local returned no completion.");

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply.Choices[0].Message.Content ?? ""))
        {
            ModelId = modelId,
        };
    }

    /// <summary>
    /// Not implemented on purpose. Nothing here streams: every device prompt is a
    /// short constrained completion, and a token-by-token reveal would make the
    /// tier-to-tier latency comparison unreadable.
    /// </summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException("FabPad does not stream — see FoundryChatClient.");

    static BetalgoMessage Translate(ChatMessage m) =>
        m.Role == ChatRole.System ? BetalgoMessage.FromSystem(m.Text)
        : m.Role == ChatRole.Assistant ? BetalgoMessage.FromAssistant(m.Text)
        : BetalgoMessage.FromUser(m.Text);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
