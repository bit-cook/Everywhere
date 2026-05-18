using Avalonia.Controls;
using Everywhere.AI;
using Everywhere.Chat;

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

    private ChatMessageControl? _lastMessageControl;

    protected override void ContainerIndexChangedOverride(Control container, int oldIndex, int newIndex)
    {
        base.ContainerIndexChangedOverride(container, oldIndex, newIndex);

        UpdateLastMessageControl(container, newIndex);
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return item switch
        {
            ChatMessageNode chatMessageNode => new ChatMessageControl
            {
                DataContext = chatMessageNode,
                Content = chatMessageNode.Message,
            },
            ChatMessage chatMessage => new ChatMessageControl
            {
                DataContext = chatMessage,
                Content = chatMessage,
            },
            _ => base.CreateContainerForItemOverride(item, index, recycleKey)
        };
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);

        UpdateLastMessageControl(container, index);
    }

    private void UpdateLastMessageControl(Control control, int index)
    {
        if (control is ChatMessageControl chatMessageControl && index == Items.Count - 1)
        {
            _lastMessageControl?.IsLast = false;
            _lastMessageControl = chatMessageControl;
            _lastMessageControl.IsLast = true;
        }
    }
}