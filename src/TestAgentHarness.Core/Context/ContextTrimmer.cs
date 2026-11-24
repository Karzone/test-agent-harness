using TestAgentHarness.Model;

namespace TestAgentHarness.Context;

/// <summary>
/// Keeps the conversation from growing without limit. Long contexts cost more,
/// run slower and make models worse ("context rot").
///
/// The system prompt and the task (the first two messages) are always kept. The
/// oldest turns after them are dropped first. A turn is dropped whole: a tool
/// result is never left behind without the assistant message that asked for it,
/// because chat APIs reject that.
/// </summary>
public static class ContextTrimmer
{
    private const int Pinned = 2;

    /// <summary>Trims in place. Returns true if anything was dropped.</summary>
    public static bool Trim(List<ChatMessage> messages, int maxMessages)
    {
        if (messages.Count <= maxMessages) return false;

        var drop = messages.Count - maxMessages;

        // Extend the cut so the first message kept is not an orphaned tool result.
        while (Pinned + drop < messages.Count && messages[Pinned + drop].Role == "tool") drop++;

        messages.RemoveRange(Pinned, drop);
        return true;
    }
}
