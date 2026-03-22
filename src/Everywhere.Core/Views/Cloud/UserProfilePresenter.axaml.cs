using System.Globalization;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Everywhere.Cloud;

namespace Everywhere.Views;

public class UserProfilePresenter : TemplatedControl
{
    public static readonly StyledProperty<UserProfile?> UserProfileProperty =
        AvaloniaProperty.Register<UserProfilePresenter, UserProfile?>(nameof(UserProfile));

    public UserProfile? UserProfile
    {
        get => GetValue(UserProfileProperty);
        set => SetValue(UserProfileProperty, value);
    }

    public static readonly StyledProperty<SubscriptionInformation?> SubscriptionProperty =
        AvaloniaProperty.Register<UserProfilePresenter, SubscriptionInformation?>(nameof(Subscription));

    public SubscriptionInformation? Subscription
    {
        get => GetValue(SubscriptionProperty);
        set => SetValue(SubscriptionProperty, value);
    }

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<UserProfilePresenter, double>(nameof(Size), 16d);

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public static readonly StyledProperty<bool> ShowActionsButtonProperty =
        UserProfileCard.ShowActionsButtonProperty.AddOwner<UserProfilePresenter>();

    public bool ShowActionsButton
    {
        get => GetValue(ShowActionsButtonProperty);
        set => SetValue(ShowActionsButtonProperty, value);
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