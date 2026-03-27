using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat;

/// <summary>
/// A frame that contains all context duration one function calling
/// </summary>
public sealed record FunctionCallContext(
    Kernel Kernel,
    ChatContext ChatContext,
    ChatPlugin ChatPlugin,
    ChatFunction ChatFunction,
    FunctionCallChatMessage FunctionCallChatMessage,
    IDictionary<string,bool> IsPermissionGrantedRecords
) : IChatPluginUserInterface
{
    public string PermissionKey => $"{ChatPlugin.Key}.{ChatFunction.KernelFunction.Name}";

    public bool IsPermissionGranted
    {
        get
        {
            var permissionKey = PermissionKey;

            // If the function requires permissions that are less than FileAccess, we consider it as low-risk and grant permission by default.
            if (ChatFunction.Permissions <= ChatFunctionPermissions.AutoGranted &&
                (!ChatContext.IsPermissionGrantedRecords.ContainsKey(permissionKey) || ChatContext.IsPermissionGrantedRecords[permissionKey]))
            {
                return true;
            }

            ChatContext.IsPermissionGrantedRecords.TryGetValue(permissionKey, out var isSessionGranted);
            return isSessionGranted;
        }
    }

    #region IChatPluginUserInterface implementation

    public IChatPluginDisplaySink DisplaySink => FunctionCallChatMessage.DisplaySink;

    public async Task<bool> RequestConsentAsync(
        string? id,
        IDynamicResourceKey headerKey,
        ChatPluginDisplayBlock? content = null,
        CancellationToken cancellationToken = default)
    {
        string? permissionKey = null;
        if (!id.IsNullOrWhiteSpace())
        {
            permissionKey = $"{PermissionKey}.{id}";
            IsPermissionGrantedRecords.TryGetValue(permissionKey, out var isGloballyGranted);
            ChatContext.IsPermissionGrantedRecords.TryGetValue(permissionKey, out var isSessionGranted);
            if (isGloballyGranted || isSessionGranted)
            {
                return true;
            }
        }

        var promise = new TaskCompletionSource<ConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        WeakReferenceMessenger.Default.Send(
            new ChatPluginRequestConsentMessage(
                promise,
                headerKey,
                content,
                permissionKey is not null,
                cancellationToken));

        var consentDecision = await promise.Task;

        if (permissionKey is null)
        {
            // no id provided, so we cannot remember the decision
            return consentDecision switch
            {
                ConsentDecision.AllowOnce => true,
                _ => false,
            };
        }

        switch (consentDecision)
        {
            case ConsentDecision.AlwaysAllow:
            {
                IsPermissionGrantedRecords[permissionKey] = true;
                return true;
            }
            case ConsentDecision.AllowSession:
            {
                ChatContext.IsPermissionGrantedRecords[permissionKey] = true;
                return true;
            }
            case ConsentDecision.AllowOnce:
            {
                return true;
            }
            case ConsentDecision.Deny:
            default:
            {
                return false;
            }
        }
    }

    public Task<IReadOnlyList<ChatPluginQuestionAnswer>> AskQuestionAsync(
        IReadOnlyList<ChatPluginQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        var promise = new TaskCompletionSource<IReadOnlyList<ChatPluginQuestionAnswer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        WeakReferenceMessenger.Default.Send(
            new ChatPluginAskQuestionMessage(
                promise,
                questions,
                cancellationToken));

        return promise.Task;
    }

    #endregion
}