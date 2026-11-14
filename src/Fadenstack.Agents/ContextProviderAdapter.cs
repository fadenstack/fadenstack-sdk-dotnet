using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Fadenstack.Agents;

/// <summary>
/// Calls a Microsoft Agent Framework <see cref="AIContextProvider"/> (skills, later compaction) from our own loop, before a
/// turn and after it (sdk/51 F02, I02 commit 12). MAF documents no pattern for a custom loop, and the contexts' constructors
/// are experimental (MAAI001): this class is the only place that uses them, so a change in MAF changes only this file, and
/// our own thin loader is the fallback (H9). Unused until F04.
/// </summary>
internal sealed class ContextProviderAdapter
{
    private readonly AIContextProvider _provider;

    public ContextProviderAdapter(AIContextProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>What the provider adds before the model is called: instructions, messages, tools.</summary>
    public async Task<AIContext> BeforeTurnAsync(AIAgent agent, Microsoft.Agents.AI.AgentSession session, AIContext context,
        CancellationToken cancellationToken)
    {
#pragma warning disable MAAI001 // the context constructors are experimental in MAF 1.28; kept to this class
        var invoking = new AIContextProvider.InvokingContext(agent, session, context);
#pragma warning restore MAAI001
        return await _provider.InvokingAsync(invoking, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tells the provider how the turn went: the request and the answer, or the exception that ended it.</summary>
    public async Task AfterTurnAsync(AIAgent agent, Microsoft.Agents.AI.AgentSession session, IEnumerable<ChatMessage> request,
        IEnumerable<ChatMessage>? response, Exception? failure, CancellationToken cancellationToken)
    {
#pragma warning disable MAAI001
        var invoked = failure is null
            ? new AIContextProvider.InvokedContext(agent, session, request, response ?? Array.Empty<ChatMessage>())
            : new AIContextProvider.InvokedContext(agent, session, request, failure);
#pragma warning restore MAAI001
        await _provider.InvokedAsync(invoked, cancellationToken).ConfigureAwait(false);
    }
}
