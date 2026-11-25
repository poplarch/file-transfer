using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using FileTransfer.App.Services;
using FileTransfer.App.ViewModels;

namespace FileTransfer.App.Views;

/// <summary>
/// Encapsulates DataGrid visual-tree hit testing, Vim key navigation, and Explorer++-style type-ahead jumping.
/// </summary>
internal sealed class GridNavigationHelper
{
    private string _typeAheadBuffer = string.Empty;
    private DateTime _lastTypeAheadUtc = DateTime.MinValue;
    private object? _lastTypeAheadGrid;

    public static T? FindDataContext<T>(object? source) where T : class
    {
        var control = source as Control;
        while (control is not null)
        {
            if (control.DataContext is T item)
            {
                return item;
            }
            control = control.Parent as Control;
        }
        return null;
    }

    public static bool IsInsideControl<T>(object? source) where T : class
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is T)
            {
                return true;
            }
            visual = visual.GetVisualParent();
        }
        return false;
    }

    public static void FocusGridWithSelection<T>(DataGrid grid, IReadOnlyList<T> items)
    {
        if (grid.SelectedItem is null && items.Count > 0)
        {
            grid.SelectedIndex = 0;
            grid.ScrollIntoView(items[0], null);
        }
        grid.Focus();
    }

    public static bool IsSlashSearchKey(KeyEventArgs e) =>
        (e.Key == Key.OemQuestion && e.KeyModifiers == KeyModifiers.None) ||
        (e.Key == Key.Divide && e.KeyModifiers == KeyModifiers.None);

    public static async Task<bool> TryHandleVimNavigationAsync<T>(
        KeyEventArgs e,
        DataGrid grid,
        IReadOnlyList<T> items,
        Func<Task> onParent,
        Func<T, Task> onActivate) where T : class
    {
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.J when e.KeyModifiers == KeyModifiers.None:
                MoveGridSelection(grid, items, +1);
                return true;
            case Key.K when e.KeyModifiers == KeyModifiers.None:
                MoveGridSelection(grid, items, -1);
                return true;
            case Key.H when e.KeyModifiers == KeyModifiers.None:
                await onParent();
                return true;
            case Key.L when e.KeyModifiers == KeyModifiers.None:
                if (grid.SelectedItem is T selected)
                {
                    await onActivate(selected);
                }
                return true;
            case Key.G when e.KeyModifiers == KeyModifiers.None:
                SelectGridIndex(grid, items, 0);
                return true;
            case Key.G when e.KeyModifiers == KeyModifiers.Shift:
                SelectGridIndex(grid, items, items.Count - 1);
                return true;
            default:
                return false;
        }
    }

    public static bool TryGetTypeAheadChar(KeyEventArgs e, out char ch)
    {
        ch = '\0';
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
        {
            return false;
        }

        if (e.Key is >= Key.A and <= Key.Z)
        {
            ch = (char)('a' + (e.Key - Key.A));
            return true;
        }

        if (e.Key is >= Key.D0 and <= Key.D9)
        {
            ch = (char)('0' + (e.Key - Key.D0));
            return true;
        }

        if (e.Key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            ch = (char)('0' + (e.Key - Key.NumPad0));
            return true;
        }

        return false;
    }

    public void HandleTypeAhead<T>(
        DataGrid grid,
        IReadOnlyList<T> items,
        Func<T, string> nameSelector,
        char ch,
        MainWindowViewModel vm)
    {
        if (items.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var expired = _lastTypeAheadGrid != grid || (now - _lastTypeAheadUtc).TotalMilliseconds > 900;
        _lastTypeAheadGrid = grid;
        _lastTypeAheadUtc = now;

        bool advanceToNext;
        string query;

        if (expired)
        {
            _typeAheadBuffer = ch.ToString();
            query = _typeAheadBuffer;
            advanceToNext = true;
        }
        else if (_typeAheadBuffer.Length == 1 && _typeAheadBuffer[0] == ch)
        {
            query = _typeAheadBuffer;
            advanceToNext = true;
        }
        else
        {
            _typeAheadBuffer += ch;
            query = _typeAheadBuffer;
            advanceToNext = false;
        }

        var currentIndex = grid.SelectedIndex;
        var matchIndex = PinyinSearchHelper.FindNextMatchIndex(items, nameSelector, query, currentIndex, advanceToNext);

        if (matchIndex < 0 && query.Length > 1)
        {
            _typeAheadBuffer = ch.ToString();
            query = _typeAheadBuffer;
            matchIndex = PinyinSearchHelper.FindNextMatchIndex(items, nameSelector, query, currentIndex, true);
        }

        if (matchIndex >= 0)
        {
            var target = items[matchIndex];
            grid.SelectedItem = target;
            grid.ScrollIntoView(target, null);
            if (!vm.IsTransferring)
            {
                vm.TransferStage = $"快速定位：{query}";
            }
        }
    }

    private static IReadOnlyList<T> GetOrderedItems<T>(DataGrid grid, IReadOnlyList<T> fallback)
    {
        if (grid.CollectionView is not null)
        {
            var ordered = grid.CollectionView.Cast<object>().OfType<T>().ToList();
            if (ordered.Count > 0)
            {
                return ordered;
            }
        }
        return fallback;
    }

    private static void MoveGridSelection<T>(DataGrid grid, IReadOnlyList<T> items, int delta)
    {
        var ordered = GetOrderedItems(grid, items);
        if (ordered.Count == 0)
        {
            return;
        }

        var currentIndex = grid.SelectedItem is T selected
            ? FindIndex(ordered, selected)
            : grid.SelectedIndex;

        var nextIndex = currentIndex < 0
            ? (delta > 0 ? 0 : ordered.Count - 1)
            : Math.Clamp(currentIndex + delta, 0, ordered.Count - 1);

        SelectOrderedIndex(grid, ordered, nextIndex);
    }

    private static void SelectGridIndex<T>(DataGrid grid, IReadOnlyList<T> items, int index)
    {
        var ordered = GetOrderedItems(grid, items);
        SelectOrderedIndex(grid, ordered, index);
    }

    private static void SelectOrderedIndex<T>(DataGrid grid, IReadOnlyList<T> ordered, int index)
    {
        if (ordered.Count == 0)
        {
            return;
        }

        var clamped = Math.Clamp(index, 0, ordered.Count - 1);
        var target = ordered[clamped];
        grid.SelectedItems.Clear();
        grid.SelectedItem = target;
        grid.ScrollIntoView(target, null);
    }

    private static int FindIndex<T>(IReadOnlyList<T> ordered, T target)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(ordered[i], target))
            {
                return i;
            }
        }
        return -1;
    }
}
