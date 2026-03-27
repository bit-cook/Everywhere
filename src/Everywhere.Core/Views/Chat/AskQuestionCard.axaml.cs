using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Plugins;
using ShadUI;
using ZLinq;

namespace Everywhere.Views;

public partial class AskQuestionCard : Card
{
    public abstract partial class OptionWrapper : ObservableObject
    {
        [ObservableProperty]
        public partial bool IsSelected { get; set; }
    }

    public sealed class NormalOptionWrapper(ChatPluginQuestionOption option) : OptionWrapper
    {
        public ChatPluginQuestionOption Option { get; } = option;
    }

    public sealed partial class FreeformOptionWrapper : OptionWrapper
    {
        [ObservableProperty]
        public partial string? FreeformText { get; set; }
    }

    public sealed class QuestionWrapper
    {
        public ChatPluginQuestion Question { get; }
        public SelectionMode SelectionMode { get; }
        public List<OptionWrapper> OptionWrappers { get; } = [];

        public QuestionWrapper(ChatPluginQuestion question)
        {
            Question = question;
            var isMultiSelect = question.MultiSelect;
            SelectionMode = isMultiSelect ? SelectionMode.Multiple : SelectionMode.Single;

            var hasPreSelected = false;
            if (question.Options is not null)
            {
                foreach (var option in question.Options)
                {
                    var isSelected = option.Recommended && (isMultiSelect || !hasPreSelected);
                    if (isSelected) hasPreSelected = true;

                    OptionWrappers.Add(
                        new NormalOptionWrapper(option)
                        {
                            IsSelected = isSelected
                        });
                }
            }

            if (question.AllowFreeformInput)
            {
                OptionWrappers.Add(new FreeformOptionWrapper());
            }
        }
    }

    #region Properties

    public static readonly StyledProperty<IReadOnlyList<ChatPluginQuestion>?> QuestionsProperty =
        AvaloniaProperty.Register<AskQuestionCard, IReadOnlyList<ChatPluginQuestion>?>(nameof(Questions));

    public IReadOnlyList<ChatPluginQuestion>? Questions
    {
        get => GetValue(QuestionsProperty);
        set => SetValue(QuestionsProperty, value);
    }

    public static readonly DirectProperty<AskQuestionCard, IReadOnlyList<QuestionWrapper>?> WrappedQuestionsProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, IReadOnlyList<QuestionWrapper>?>(
            nameof(WrappedQuestions),
            o => o.WrappedQuestions);

    public IReadOnlyList<QuestionWrapper>? WrappedQuestions
    {
        get;
        private set => SetAndRaise(WrappedQuestionsProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, QuestionWrapper?> CurrentQuestionProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, QuestionWrapper?>(
            nameof(CurrentQuestion),
            o => o.CurrentQuestion);

    public QuestionWrapper? CurrentQuestion
    {
        get;
        private set => SetAndRaise(CurrentQuestionProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, string?> PageDisplayProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, string?>(
            nameof(PageDisplay),
            o => o.PageDisplay);

    public string? PageDisplay
    {
        get;
        private set => SetAndRaise(PageDisplayProperty, ref field, value);
    }

    public static readonly DirectProperty<AskQuestionCard, bool> ShowPaginationProperty =
        AvaloniaProperty.RegisterDirect<AskQuestionCard, bool>(
            nameof(ShowPagination),
            o => o.ShowPagination);

    public bool ShowPagination
    {
        get;
        private set => SetAndRaise(ShowPaginationProperty, ref field, value);
    }

    #endregion

    #region Events

    public delegate void SubmittedEventHandler(IReadOnlyList<ChatPluginQuestionAnswer> answers);

    public event SubmittedEventHandler? Submitted;

    #endregion

    private int _currentIndex;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != QuestionsProperty) return;

        var questions = change.GetNewValue<IReadOnlyList<ChatPluginQuestion>?>();
        if (questions is null or { Count: 0 })
        {
            WrappedQuestions = null;
            CurrentQuestion = null;
            ShowPagination = false;
            PageDisplay = null;
            return;
        }

        WrappedQuestions = questions.AsValueEnumerable().Select(q => new QuestionWrapper(q)).ToList();
        ShowPagination = WrappedQuestions.Count > 1;
        NavigateTo(0);
    }

    private void NavigateTo(int index)
    {
        if (WrappedQuestions is null) return;
        _currentIndex = index;
        CurrentQuestion = WrappedQuestions[index];
        PageDisplay = $"{index + 1} / {WrappedQuestions.Count}";
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (_currentIndex > 0) NavigateTo(_currentIndex - 1);
    }

    [RelayCommand]
    private void NextPage()
    {
        if (WrappedQuestions is null) return;

        if (_currentIndex < WrappedQuestions.Count - 1)
        {
            NavigateTo(_currentIndex + 1);
        }
        else if (Submitted is { } submitted)
        {
            var answers = new ChatPluginQuestionAnswer[WrappedQuestions.Count];
            for (var i = 0; i < WrappedQuestions.Count; i++)
            {
                var question = WrappedQuestions[i];

                var selected = new List<string>();
                string? freeformText = null;
                foreach (var optionWrapper in question.OptionWrappers)
                {
                    if (optionWrapper is NormalOptionWrapper { IsSelected: true, Option: { } option })
                    {
                        selected.Add(option.Label);
                    }
                    else if (optionWrapper is FreeformOptionWrapper { FreeformText: { Length: > 0 } freeform })
                    {
                        freeformText = freeform;
                        break;
                    }
                }

                answers[i] = new ChatPluginQuestionAnswer(selected, freeformText);
            }

            submitted(answers);
        }
    }
}