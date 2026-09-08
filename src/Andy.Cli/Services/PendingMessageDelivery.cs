using Andy.Model.Model;

namespace Andy.Cli.Services;

/// <summary>Prepares one pending snapshot for the Engine, restoring it if preparation fails.</summary>
internal sealed class PendingMessageDelivery(
    Func<IReadOnlyList<PendingUserMessage>> take,
    Action<IReadOnlyList<PendingUserMessage>> restore,
    Func<PendingUserMessage, CancellationToken, Task<IReadOnlyList<MessagePart>>> prepare,
    Action<IReadOnlyList<PendingUserMessage>> accepted)
{
    public async Task<IReadOnlyList<IReadOnlyList<MessagePart>>> TakeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var messages = take();
        var parts = new List<IReadOnlyList<MessagePart>>(messages.Count);
        try
        {
            foreach (var message in messages) parts.Add(await prepare(message, ct));
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            restore(messages);
            throw;
        }
        accepted(messages);
        return parts;
    }
}
