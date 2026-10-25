using ModelExplorer.Indexing;

namespace ModelExplorer.Core.Library;

/// <summary>
/// Which search results are selected. The selection is what the viewer shows: one
/// model on its own, several side by side.
/// </summary>
/// <remarks>
/// Held as the <see cref="ModelFile"/> instances themselves, not as row numbers.
/// The results are replaced on every keystroke in the search box, and the search
/// snapshot hands out the same instances to every query, so a file that is still
/// in the new results stays selected without any lookup by path.
/// </remarks>
public sealed class ResultSelection
{
    private readonly HashSet<ModelFile> _selected = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<ModelFile> _items = [];
    private IReadOnlyList<ModelFile>? _ordered;

    /// <summary>Changes whenever the selection does, so a caller can tell cheaply.</summary>
    public int Version { get; private set; }

    /// <summary>The row keyboard navigation moves from, or -1.</summary>
    public int Focus { get; private set; } = -1;

    /// <summary>The row a Shift+click extends from, or -1.</summary>
    public int Anchor { get; private set; } = -1;

    public int Count => _selected.Count;

    /// <summary>The selected files in the order of the results.</summary>
    public IReadOnlyList<ModelFile> Selected
    {
        get
        {
            if (_ordered is null)
            {
                var ordered = new List<ModelFile>(_selected.Count);
                if (_selected.Count > 0)
                {
                    foreach (var item in _items)
                    {
                        if (_selected.Contains(item))
                        {
                            ordered.Add(item);
                            if (ordered.Count == _selected.Count)
                            {
                                break;
                            }
                        }
                    }
                }

                _ordered = ordered;
            }

            return _ordered;
        }
    }

    public bool IsSelected(ModelFile item) => _selected.Contains(item);

    /// <summary>
    /// Takes a new result list. Selected files that are still in it stay selected;
    /// the others are dropped, because a selection nobody can see would keep
    /// models on screen that the list no longer explains.
    /// </summary>
    public void Reset(IReadOnlyList<ModelFile> items)
    {
        var focused = Focus >= 0 && Focus < _items.Count ? _items[Focus] : null;
        var anchored = Anchor >= 0 && Anchor < _items.Count ? _items[Anchor] : null;
        _items = items;
        Focus = Anchor = -1;

        var kept = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (ReferenceEquals(item, focused))
            {
                Focus = i;
            }

            if (ReferenceEquals(item, anchored))
            {
                Anchor = i;
            }

            if (_selected.Contains(item))
            {
                kept++;
            }
        }

        if (kept != _selected.Count)
        {
            var present = new HashSet<ModelFile>(items, ReferenceEqualityComparer.Instance);
            _selected.RemoveWhere(item => !present.Contains(item));
            Changed();
        }
        else
        {
            _ordered = null;
        }
    }

    /// <summary>
    /// A click on row <paramref name="index"/>: on its own it selects just that row,
    /// with Ctrl it adds or removes the row, with Shift it selects the range from the
    /// anchor (adding to the selection when Ctrl is held too).
    /// </summary>
    public void Click(int index, bool control, bool shift)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        if (shift && Anchor >= 0 && Anchor < _items.Count)
        {
            if (!control)
            {
                _selected.Clear();
            }

            for (var i = Math.Min(Anchor, index); i <= Math.Max(Anchor, index); i++)
            {
                _selected.Add(_items[i]);
            }
        }
        else if (control)
        {
            if (!_selected.Remove(_items[index]))
            {
                _selected.Add(_items[index]);
            }

            Anchor = index;
        }
        else
        {
            _selected.Clear();
            _selected.Add(_items[index]);
            Anchor = index;
        }

        Focus = index;
        Changed();
    }

    /// <summary>Moves the focus by <paramref name="delta"/> rows, selecting it alone or, with Shift, extending to it.</summary>
    public void Move(int delta, bool extend)
    {
        if (_items.Count == 0)
        {
            return;
        }

        var from = Focus >= 0 ? Focus : delta > 0 ? -1 : _items.Count;
        Click(Math.Clamp(from + delta, 0, _items.Count - 1), control: false, shift: extend);
    }

    public void SelectAll()
    {
        if (_items.Count == 0)
        {
            return;
        }

        foreach (var item in _items)
        {
            _selected.Add(item);
        }

        Changed();
    }

    public void Clear()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        _selected.Clear();
        Changed();
    }

    /// <summary>
    /// Makes <paramref name="item"/> the selection unless it is already part of it —
    /// what a right-click does, so a context menu acts on a selection it belongs to.
    /// </summary>
    public void Ensure(int index)
    {
        if (index >= 0 && index < _items.Count && !_selected.Contains(_items[index]))
        {
            Click(index, control: false, shift: false);
        }
    }

    private void Changed()
    {
        _ordered = null;
        Version++;
    }
}
