using Everywhere.Chat.Plugins;

namespace Everywhere.Messages;

/// <summary>
/// Raised by <see cref="IChatPluginUserInterface"/> when a plugin needs to ask the user a question and get the answer asynchronously.
/// The message contains a Promise that the UI can use to return the user's answers asynchronously.
/// The UI should display the provided list of questions to the user and collect their answers.
/// The operation can be canceled using the provided <see cref="CancellationToken"/>.
/// </summary>
/// <param name="Promise"></param>
/// <param name="Questions"></param>
/// <param name="CancellationToken"></param>
public sealed record ChatPluginAskQuestionMessage(
    TaskCompletionSource<IReadOnlyList<ChatPluginQuestionAnswer>> Promise,
    IReadOnlyList<ChatPluginQuestion> Questions,
    CancellationToken CancellationToken
);