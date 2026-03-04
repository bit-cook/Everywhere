using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;

namespace Everywhere.Views;

public partial class UserProfilePresenter : TemplatedControl
{
    public static readonly StyledProperty<UserProfile?> UserProfileProperty =
        AvaloniaProperty.Register<UserProfilePresenter, UserProfile?>(nameof(UserProfile));

    public UserProfile? UserProfile
    {
        get => GetValue(UserProfileProperty);
        set => SetValue(UserProfileProperty, value);
    }

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<UserProfilePresenter, double>(nameof(Size), 16d);

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public static readonly StyledProperty<bool> ShowActionsButtonProperty =
        AvaloniaProperty.Register<UserProfilePresenter, bool>(nameof(ShowActionsButton), true);

    public bool ShowActionsButton
    {
        get => GetValue(ShowActionsButtonProperty);
        set => SetValue(ShowActionsButtonProperty, value);
    }

    public PersistentState PersistentState { get; } = ServiceLocator.Resolve<PersistentState>();

    private readonly ICloudClient _cloudClient = ServiceLocator.Resolve<ICloudClient>();

    [RelayCommand]
    private Task<bool> LoginAsync(CancellationToken cancellationToken) =>
        _cloudClient.LoginAsync(cancellationToken);

    [RelayCommand]
    private Task LogoutAsync(CancellationToken cancellationToken) =>
        _cloudClient.LogoutAsync(cancellationToken);

    [RelayCommand]
    private Task<bool> OpenDashboardAsync() => OpenUrlAsync(CloudConstants.DashboardUrl);

    [RelayCommand]
    private Task<bool> ManageSubscriptionAsync() => OpenUrlAsync(CloudConstants.ManageSubscriptionUrl);

    [RelayCommand]
    private Task<bool> GetSupportAsync() => OpenUrlAsync(CloudConstants.GetSupportUrl);

    private Task<bool> OpenUrlAsync(string url)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            return launcher.LaunchUriAsync(new Uri(url));
        }

        return Task.FromResult(false);
    }
}

public sealed class SubscriptionPlanToBrushValueConverter : IValueConverter
{
    public static SubscriptionPlanToBrushValueConverter Shared { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SubscriptionPlan plan)
        {
            return plan switch
            {
                SubscriptionPlan.Free => Brushes.Gray,
                SubscriptionPlan.Starter => new SolidColorBrush(new Color(0xff, 0x0e, 0x27, 0x52)),
                SubscriptionPlan.Plus => new SolidColorBrush(new Color(0xff, 0x35, 0x15, 0x52)),
                SubscriptionPlan.Pro => new SolidColorBrush(new Color(0xff, 0x52, 0x32, 0x00)),
                _ => Brushes.Transparent
            };
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}