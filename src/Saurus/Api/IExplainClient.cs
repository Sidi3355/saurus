namespace Saurus.Api;

/// <summary>
/// The seam between the orchestrator and whatever actually produces an explanation.
///
/// Exists so the offline test client can be swapped in without the orchestrator, the
/// guardrails or the UI knowing the difference — every other layer runs identically in
/// both modes, which is the whole point of being able to test without a key.
/// </summary>
public interface IExplainClient : IDisposable
{
    /// <summary>False means the guardrail chain will refuse with a setup message.</summary>
    bool HasKey { get; }

    Task<ExplainResult> StreamAsync(
        string model,
        int maxTokens,
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        Action<string> onDelta,
        TimeSpan timeout,
        CancellationToken ct = default);
}
