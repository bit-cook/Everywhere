using LiveMarkdown.Avalonia;

namespace Everywhere.Common;

public sealed class ThreadSafeObservableStringBuilder : ObservableStringBuilder
{
    private readonly Lock _lock = new();

    public new int Length
    {
        get
        {
            using var _ = _lock.EnterScope();
            return base.Length;
        }
    }

    public new ThreadSafeObservableStringBuilder Append(string? value)
    {
        using var _ = _lock.EnterScope();
        base.Append(value);
        return this;
    }

    public new ThreadSafeObservableStringBuilder AppendLine(string? value = null)
    {
        using var _ = _lock.EnterScope();
        base.AppendLine(value);
        return this;
    }

    public new ThreadSafeObservableStringBuilder Clear()
    {
        using var _ = _lock.EnterScope();
        base.Clear();
        return this;
    }

    public override string ToString()
    {
        using var _ = _lock.EnterScope();
        return base.ToString();
    }
}