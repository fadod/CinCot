namespace CInCot.Core;

/// <summary>
/// The editable state of one capture: the selection and an ordered list of annotations. Every change is
/// an edit object, so undo and redo are just moves between two stacks; pixels are only flattened on export.
/// </summary>
internal sealed class AnnotationDocument : IDisposable
{
    private readonly List<Annotation> _items = new();
    private readonly Stack<IEdit> _undo = new();
    private readonly Stack<IEdit> _redo = new();

    public Rectangle Selection { get; private set; }
    public IReadOnlyList<Annotation> Items => _items;
    public bool IsEmpty => _items.Count == 0;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasRedactions => _items.Any(a => a.IsRedaction);

    /// <summary>Sets the starting selection; not undoable.</summary>
    public void Reset(Rectangle selection) => Selection = selection;

    public void Add(Annotation annotation) => Execute(new AddEdit(annotation));

    /// <summary>
    /// Swaps <paramref name="current"/> for a restyled copy. Consecutive restyles of the same shape merge
    /// into one undo step, so scrolling the thickness up and down undoes back to the shape as drawn.
    /// </summary>
    public void Replace(Annotation current, Annotation replacement)
    {
        int index = _items.IndexOf(current);
        if (index < 0)
        {
            replacement.Dispose();
            return;
        }
        if (_undo.TryPeek(out var top) && top is ReplaceEdit edit && ReferenceEquals(edit.Current, current))
        {
            _items[index] = replacement;
            current.Dispose();
            edit.Current = replacement;
            foreach (var dropped in _redo) dropped.Discard();
            _redo.Clear();
            return;
        }
        Execute(new ReplaceEdit(current, replacement));
    }

    /// <summary>Adds several annotations as one undo step.</summary>
    public void AddRange(IEnumerable<Annotation> annotations)
    {
        var edits = annotations.Select(a => new AddEdit(a)).ToList();
        if (edits.Count > 0) Execute(new GroupEdit(edits));
    }

    public void SetSelection(Rectangle selection)
    {
        if (selection != Selection) Execute(new SelectionEdit(Selection, selection));
    }

    public bool Undo()
    {
        if (!_undo.TryPop(out var edit)) return false;
        edit.Revert(this);
        _redo.Push(edit);
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var edit)) return false;
        edit.Apply(this);
        _undo.Push(edit);
        return true;
    }

    /// <summary>Redactions first, from the source pixels; everything else on top in the order drawn.</summary>
    public void Render(Graphics g)
    {
        foreach (var a in _items)
            if (a.IsRedaction) a.Render(g);
        foreach (var a in _items)
            if (!a.IsRedaction) a.Render(g);
    }

    private void Execute(IEdit edit)
    {
        edit.Apply(this);
        _undo.Push(edit);
        foreach (var dropped in _redo) dropped.Discard();
        _redo.Clear();
    }

    public void Dispose()
    {
        foreach (var a in _items) a.Dispose();
        foreach (var e in _redo) e.Discard();
        _items.Clear();
        _undo.Clear();
        _redo.Clear();
    }

    private interface IEdit
    {
        void Apply(AnnotationDocument doc);
        void Revert(AnnotationDocument doc);
        void Discard();
    }

    private sealed class AddEdit(Annotation annotation) : IEdit
    {
        public void Apply(AnnotationDocument doc) => doc._items.Add(annotation);
        public void Revert(AnnotationDocument doc) => doc._items.Remove(annotation);
        public void Discard() => annotation.Dispose();
    }

    private sealed class ReplaceEdit(Annotation original, Annotation current) : IEdit
    {
        public Annotation Current { get; set; } = current;

        public void Apply(AnnotationDocument doc) => Swap(doc, original, Current);
        public void Revert(AnnotationDocument doc) => Swap(doc, Current, original);

        // Only ever discarded from the redo stack, i.e. while the restyled copy is not in the document.
        public void Discard() => Current.Dispose();

        private static void Swap(AnnotationDocument doc, Annotation from, Annotation to)
        {
            int index = doc._items.IndexOf(from);
            if (index >= 0) doc._items[index] = to;
        }
    }

    private sealed class GroupEdit(IReadOnlyList<IEdit> edits) : IEdit
    {
        public void Apply(AnnotationDocument doc)
        {
            foreach (var e in edits) e.Apply(doc);
        }

        public void Revert(AnnotationDocument doc)
        {
            for (int i = edits.Count - 1; i >= 0; i--) edits[i].Revert(doc);
        }

        public void Discard()
        {
            foreach (var e in edits) e.Discard();
        }
    }

    private sealed class SelectionEdit(Rectangle from, Rectangle to) : IEdit
    {
        public void Apply(AnnotationDocument doc) => doc.Selection = to;
        public void Revert(AnnotationDocument doc) => doc.Selection = from;
        public void Discard() { }
    }
}
