using System.Collections;

namespace Genie.Core.Mapper;

/// <summary>
/// A room's exits, safe to read on one thread while another changes them.
/// </summary>
/// <remarks>
/// The automapper adds and removes exits on the game thread while the map
/// panel and canvas enumerate them on the UI thread. With a plain
/// <c>List&lt;MapExit&gt;</c> that enumeration threw "Collection was modified"
/// and closed the app (public #398).
/// <para>
/// This list is copy-on-write: every change builds a new array under a lock and
/// swaps it in, and every read works on the array that was current when the
/// read began. A <c>foreach</c> therefore never sees a change made after it
/// started. Rooms hold a handful of exits and change rarely, so the copy per
/// write costs nothing that matters.
/// </para>
/// <para>
/// Only the list is covered. A check followed by a change (<c>GetExit</c> then
/// <c>Add</c>) is still two steps, and the <see cref="MapExit"/> objects
/// themselves are shared, not copied.
/// </para>
/// </remarks>
public sealed class MapExitList : IList<MapExit>, IReadOnlyList<MapExit>
{
    private readonly object _gate = new();
    private volatile MapExit[] _items = Array.Empty<MapExit>();

    public int Count => _items.Length;

    public bool IsReadOnly => false;

    public MapExit this[int index]
    {
        get => _items[index];
        set
        {
            lock (_gate)
            {
                var next = (MapExit[])_items.Clone();
                next[index] = value;
                _items = next;
            }
        }
    }

    public void Add(MapExit item)
    {
        lock (_gate)
        {
            var old  = _items;
            var next = new MapExit[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = item;
            _items = next;
        }
    }

    public void AddRange(IEnumerable<MapExit> items)
    {
        var added = items.ToArray();
        if (added.Length == 0) return;
        lock (_gate)
        {
            var old  = _items;
            var next = new MapExit[old.Length + added.Length];
            Array.Copy(old, next, old.Length);
            Array.Copy(added, 0, next, old.Length, added.Length);
            _items = next;
        }
    }

    public void Insert(int index, MapExit item)
    {
        lock (_gate)
        {
            var list = new List<MapExit>(_items);
            list.Insert(index, item);
            _items = list.ToArray();
        }
    }

    public bool Remove(MapExit item)
    {
        lock (_gate)
        {
            var i = Array.IndexOf(_items, item);
            if (i < 0) return false;
            RemoveAtLocked(i);
            return true;
        }
    }

    public void RemoveAt(int index)
    {
        lock (_gate) RemoveAtLocked(index);
    }

    private void RemoveAtLocked(int index)
    {
        var list = new List<MapExit>(_items);
        list.RemoveAt(index);
        _items = list.ToArray();
    }

    /// <summary>Removes every exit <paramref name="match"/> accepts; returns
    /// how many went.</summary>
    public int RemoveAll(Predicate<MapExit> match)
    {
        lock (_gate)
        {
            var old  = _items;
            var kept = Array.FindAll(old, e => !match(e));
            if (kept.Length == old.Length) return 0;
            _items = kept;
            return old.Length - kept.Length;
        }
    }

    public void Clear()
    {
        lock (_gate) _items = Array.Empty<MapExit>();
    }

    public bool Contains(MapExit item) => Array.IndexOf(_items, item) >= 0;

    public int IndexOf(MapExit item) => Array.IndexOf(_items, item);

    public void CopyTo(MapExit[] array, int arrayIndex)
    {
        var snapshot = _items;
        Array.Copy(snapshot, 0, array, arrayIndex, snapshot.Length);
    }

    public IEnumerator<MapExit> GetEnumerator()
        => ((IEnumerable<MapExit>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
