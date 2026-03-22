using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Everywhere.Views;

public class ConditionalContentControl : ContentControl
{
    /// <summary>
    /// Defines the <see cref="Condition"/> property.
    /// </summary>
    public static readonly StyledProperty<bool?> ConditionProperty =
        AvaloniaProperty.Register<ConditionalContentControl, bool?>(nameof(Condition));

    /// <summary>
    /// Gets or sets the condition that determines which content should be displayed.
    /// </summary>
    public bool? Condition
    {
        get => GetValue(ConditionProperty);
        set => SetValue(ConditionProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="TrueContent"/> property.
    /// </summary>
    public static readonly StyledProperty<IControlTemplate?> TrueContentProperty =
        AvaloniaProperty.Register<ConditionalContentControl, IControlTemplate?>(nameof(TrueContent));

    /// <summary>
    /// Gets or sets the content template to display when <see cref="Condition"/> is true.
    /// </summary>
    public IControlTemplate? TrueContent
    {
        get => GetValue(TrueContentProperty);
        set => SetValue(TrueContentProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="FalseContent"/> property.
    /// </summary>
    public static readonly StyledProperty<IControlTemplate?> FalseContentProperty =
        AvaloniaProperty.Register<ConditionalContentControl, IControlTemplate?>(nameof(FalseContent));

    /// <summary>
    /// Gets or sets the content template to display when <see cref="Condition"/> is false.
    /// </summary>
    public IControlTemplate? FalseContent
    {
        get => GetValue(FalseContentProperty);
        set => SetValue(FalseContentProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="NullContent"/> property.
    /// </summary>
    public static readonly StyledProperty<IControlTemplate?> NullContentProperty =
        AvaloniaProperty.Register<ConditionalContentControl, IControlTemplate?>(nameof(NullContent));

    /// <summary>
    /// Gets or sets the content template to display when <see cref="Condition"/> is null.
    /// </summary>
    public IControlTemplate? NullContent
    {
        get => GetValue(NullContentProperty);
        set => SetValue(NullContentProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="ContentDataBinding"/> property.
    /// </summary>
    public static readonly StyledProperty<object?> ContentDataBindingProperty =
        AvaloniaProperty.Register<ConditionalContentControl, object?>(nameof(ContentDataBinding));

    /// <summary>
    /// Gets or sets the data context for the content of this control.
    /// If not set, the control's own DataContext is used.
    /// </summary>
    public object? ContentDataBinding
    {
        get => GetValue(ContentDataBindingProperty);
        set => SetValue(ContentDataBindingProperty, value);
    }

    public ConditionalContentControl()
    {
        ConditionProperty.Changed.AddClassHandler<ConditionalContentControl>(HandleConditionChanged);
        TrueContentProperty.Changed.AddClassHandler<ConditionalContentControl>(HandleContentChanged);
        FalseContentProperty.Changed.AddClassHandler<ConditionalContentControl>(HandleContentChanged);
        NullContentProperty.Changed.AddClassHandler<ConditionalContentControl>(HandleContentChanged);
        ContentDataBindingProperty.Changed.AddClassHandler<ConditionalContentControl>(HandleContentDataContextChanged);
    }

    private void HandleConditionChanged(ConditionalContentControl sender, AvaloniaPropertyChangedEventArgs e)
    {
        UpdateContent();
    }

    private void HandleContentChanged(ConditionalContentControl sender, AvaloniaPropertyChangedEventArgs e)
    {
        UpdateContent();
    }

    private void UpdateContent()
    {
        var content = Condition switch
        {
            true => TrueContent,
            false => FalseContent,
            _ => NullContent,
        };
        var control = content?.Build(this)?.Result;
        control?.DataContext = ContentDataBinding ?? DataContext;
        Content = control;
    }

    private void HandleContentDataContextChanged(ConditionalContentControl sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (Content is Control control) control.DataContext = e.NewValue ?? sender.DataContext;
    }
}