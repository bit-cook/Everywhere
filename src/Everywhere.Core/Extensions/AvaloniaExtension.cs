using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Everywhere.Common;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;
using ZLinq;

namespace Everywhere.Extensions;

public static class AvaloniaExtension
{
    public static AnonymousExceptionHandler ToExceptionHandler(this DialogManager dialogManager) => new((exception, message, source, lineNumber) =>
        dialogManager.CreateDialog(exception.GetFriendlyMessage().ToString() ?? "Unknown error", $"[{source}:{lineNumber}] {message ?? "Error"}"));

    public static AnonymousExceptionHandler ToExceptionHandler(this ToastManager toastManager) => new((exception, message, source, lineNumber) =>
        toastManager.CreateToast($"[{source}:{lineNumber}] {message ?? "Error"}")
            .WithContent(exception.GetFriendlyMessage().ToTextBlock())
            .DismissOnClick()
            .ShowError());

    public static TextBlock ToTextBlock(this IDynamicResourceKey dynamicResourceKey)
    {
        return new TextBlock
        {
            Classes = { nameof(DynamicResourceKey) },
            [!TextBlock.TextProperty] = dynamicResourceKey.ToBinding()
        };
    }

    public static IServiceCollection AddDialogManagerAndToastManager(this IServiceCollection services)
    {
        return services
            .AddTransient<DialogManager>(_ => TryGetHost()?.DialogHost.Manager ?? new DialogManager())
            .AddTransient<ToastManager>(_ => TryGetHost()?.ToastHost.Manager ?? new ToastManager());

        IReactiveHost? TryGetHost()
        {
            if (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime is not { } lifetime) return null;

            return lifetime.Windows.AsValueEnumerable().FirstOrDefault(w => w.IsActive) as IReactiveHost ??
                lifetime.MainWindow as IReactiveHost ??
                lifetime.Windows.AsValueEnumerable().OfType<IReactiveHost>().FirstOrDefault();
        }
    }
}