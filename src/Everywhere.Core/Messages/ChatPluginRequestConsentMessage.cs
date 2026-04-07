using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;

namespace Everywhere.Messages;

/// <summary>
/// Raised by <see cref="IChatPluginUserInterface"/> when a plugin needs to request the user's consent for a certain action, such as granting permissions or confirming a function call.
/// The message contains a <see cref="TaskCompletionSource{ConsentDecision}"/> that the UI can use to return the user's decision asynchronously.
/// The UI should also display the provided header and display block to inform the user about what they are consenting to.
/// If <see cref="CanRemember"/> is true, the UI can offer an option to remember the user's decision for future similar requests.
/// The operation can be canceled using the provided <see cref="CancellationToken"/>.
/// </summary>
/// <param name="Promise"></param>
/// <param name="HeaderKey"></param>
/// <param name="DisplayBlock"></param>
/// <param name="CanRemember"></param>
/// <param name="CancellationToken"></param>
public sealed record ChatPluginRequestConsentMessage(
    TaskCompletionSource<ConsentDecision> Promise,
    IDynamicResourceKey HeaderKey,
    ChatPluginDisplayBlock? DisplayBlock,
    bool CanRemember,
    CancellationToken CancellationToken
);