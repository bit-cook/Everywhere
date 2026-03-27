using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Permissions;
using ShadUI;

namespace Everywhere.Views;

public partial class ConsentDecisionCard : Card
{
    /// <summary>
    /// Defines the <see cref="CanRemember"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> CanRememberProperty =
        AvaloniaProperty.Register<ConsentDecisionCard, bool>(nameof(CanRemember), true);

    /// <summary>
    /// Gets or sets a value indicating whether the user can choose to remember their decision.
    /// </summary>
    public bool CanRemember
    {
        get => GetValue(CanRememberProperty);
        set => SetValue(CanRememberProperty, value);
    }

    public delegate void ConsentSelectedEventHandler(ConsentDecision decision);

    public event ConsentSelectedEventHandler? ConsentSelected;

    [RelayCommand]
    private void SelectConsent(ConsentDecision decision)
    {
        ConsentSelected?.Invoke(decision);
    }
}