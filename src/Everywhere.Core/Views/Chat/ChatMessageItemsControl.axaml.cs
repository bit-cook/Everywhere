using Avalonia.Controls;
using Everywhere.AI;

namespace Everywhere.Views;

public class ChatMessageItemsControl : ItemsControl
{
    /// <summary>
    /// Defines the <see cref="IsReadonly"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsReadonlyProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, bool>(nameof(IsReadonly));

    /// <summary>
    /// Gets or sets a value indicating whether the control is in read-only mode.
    /// </summary>
    public bool IsReadonly
    {
        get => GetValue(IsReadonlyProperty);
        set => SetValue(IsReadonlyProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="SupportedModalities"/> property.
    /// </summary>
    public static readonly StyledProperty<Modalities> SupportedModalitiesProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, Modalities>(nameof(SupportedModalities));

    /// <summary>
    /// Gets or sets the modalities supported by this control. This can be used to determine which types of content (e.g., text, images, videos) the control can display or interact with.
    /// </summary>
    public Modalities SupportedModalities
    {
        get => GetValue(SupportedModalitiesProperty);
        set => SetValue(SupportedModalitiesProperty, value);
    }
}