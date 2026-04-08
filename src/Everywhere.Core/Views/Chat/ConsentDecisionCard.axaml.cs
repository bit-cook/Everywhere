using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Permissions;
using ShadUI;

namespace Everywhere.Views;

public sealed class ConsentDecisionCard : Card
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

    public static readonly StyledProperty<IRelayCommand<ConsentDecision>?> CommandProperty =
        AvaloniaProperty.Register<ConsentDecisionCard, IRelayCommand<ConsentDecision>?>(nameof(Command));

    public IRelayCommand<ConsentDecision>? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }
}