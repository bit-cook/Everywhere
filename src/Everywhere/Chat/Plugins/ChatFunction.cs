﻿using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Lucide.Avalonia;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins;

public abstract partial class ChatFunction : ObservableObject
{
    public virtual DynamicResourceKeyBase HeaderKey => new DirectResourceKey(KernelFunction.Name);

    public LucideIconKind? Icon { get; set; }

    /// <summary>
    /// The permissions required by this function.
    /// </summary>
    public virtual ChatFunctionPermissions Permissions => ChatFunctionPermissions.AllAccess;

    /// <summary>
    /// The permissions currently granted to this function.
    /// </summary>
    [ObservableProperty]
    public partial Customizable<ChatFunctionPermissions> GrantedPermissions { get; set; } = ChatFunctionPermissions.None;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    public abstract KernelFunction KernelFunction { get; }

    /// <summary>
    /// Checks if the required permissions are granted for this function.
    /// </summary>
    /// <param name="minimumPermission">The minimum permission level to consider as granted.</param>
    /// <returns></returns>
    public bool IsPermissionsGranted(ChatFunctionPermissions minimumPermission = ChatFunctionPermissions.FileRead)
    {
        var permissions = Permissions;
        if ((int)permissions <= (int)minimumPermission) return true;

        var grantedPermissions = GrantedPermissions.ActualValue;
        return (grantedPermissions & permissions) == permissions;
    }

    /// <summary>
    /// Converts the function call content to a user-friendly format for UI display.
    /// </summary>
    /// <param name="content"></param>
    /// <returns></returns>
    public virtual ChatPluginDisplayBlock? GetFriendlyCallContent(FunctionCallContent content) => null;
}

public sealed class NativeChatFunction : ChatFunction
{
    public override DynamicResourceKeyBase HeaderKey { get; }

    public override ChatFunctionPermissions Permissions { get; }

    public override KernelFunction KernelFunction { get; }

    private readonly IFriendlyFunctionCallContentRenderer? _renderer;

    public NativeChatFunction(Delegate method, ChatFunctionPermissions permissions, LucideIconKind? icon = null)
    {
        if (method.Method.GetCustomAttributes<DynamicResourceKeyAttribute>(false).FirstOrDefault() is { Key: { Length: > 0 } key })
        {
            HeaderKey = new DynamicResourceKey(key);
        }
        else if (method.Method.GetCustomAttributes<KernelFunctionAttribute>(false).FirstOrDefault() is { Name: { Length: > 0 } name })
        {
            HeaderKey = new DirectResourceKey(name);
        }
        else
        {
            HeaderKey = new DirectResourceKey(method.Method.Name.TrimEnd("Async"));
        }

        Icon = icon;
        Permissions = permissions;
        KernelFunction = KernelFunctionFactory.CreateFromMethod(method);

        if (method.Method.GetCustomAttributes<FriendlyFunctionCallContentRendererAttribute>(false).FirstOrDefault() is { RendererType: { } rendererType })
        {
            if (!typeof(IFriendlyFunctionCallContentRenderer).IsAssignableFrom(rendererType))
            {
                throw new InvalidOperationException(
                    $"The renderer type '{rendererType.FullName}' does not implement {nameof(IFriendlyFunctionCallContentRenderer)}.");
            }

            _renderer = Activator.CreateInstance(rendererType) as IFriendlyFunctionCallContentRenderer;
        }
    }

    public override ChatPluginDisplayBlock? GetFriendlyCallContent(FunctionCallContent content)
    {
        if (content.Arguments is not { Count: > 0 } arguments) return base.GetFriendlyCallContent(content);
        return _renderer?.Render(arguments) ?? base.GetFriendlyCallContent(content);
    }
}