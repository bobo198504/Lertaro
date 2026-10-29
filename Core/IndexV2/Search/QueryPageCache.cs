using Lertaro.Core.SearchIndex;

namespace Lertaro.Core.IndexV2.Search;

// Rows of a query this drive already answered, keyed by the query and its filters. One cache belongs to
// one LiveIndex: a page is only a valid answer for the snapshot/delta pair it was scanned from, so a
// process-wide cache let a page stored for one drive -- or for an earlier index opened under the same
// drive letter -- answer another's query with rows that never existed there. Nothing is persisted;
// disposing the index drops the cache with it.
internal sealed class QueryPageCache
{
    private sealed class Entry
    {
        public List<SearchResult> Rows = new();
        public long LastUsed;
        public long StoredAt;

        // True when the scan that produced Rows ran out of matches: the page holds every row that exists
        // for this key, so it answers a request for more rows just as well as a shorter one. Without this
        // a page could only serve requests smaller than its own length, and every narrow category -- a
        // filter that matches fewer rows than the quick window asks for -- was refused on every keystroke
        // and rescanned the whole index instead. See TryReplay.
        public bool Exhausted;
    }

    // Sized to hold a working set of pages: several filters over the alphabet across every drive. A
    // smaller capacity made the store evict its own earlier entries, so nothing it had just answered
    // ever hit.
    private const int Capacity = 512;

    // How long a page stays answerable after it was written. A session budget, not a keystroke bridge:
    // the desktop changes files constantly, so a revision rule retired almost every page between two
    // searches and put the slowest miss back on the critical path. Ten minutes keeps a page alive for a
    // whole search session -- switching filters and coming back no longer rebuilds it -- and a page that
    // does outlive the window is simply rebuilt from the then-current index, which is what lets a file
    // created since then show up.
    private const long RecentGraceMs = 10 * 60_000;

    // A stored page is a prefix of its scan, never the whole drive: the full window asks for
    // int.MaxValue, and keeping that answer would pin an unbounded amount of memory per entry. A
    // prefix of this size still covers the quick window (204 candidates) and any smaller request.
    private const int MaxRows = 512;

    private readonly Dictionary<string, Entry> _pages = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public static string KeyFor(string query, string? directoryFilter, string? fileNameFilter, byte category = 0)
        // The category is part of the identity: the same text under two filters is two different pages, and
        // a page built for one filter would answer the other with rows it must not show.
        => string.Concat(query, "\u0001", directoryFilter ?? string.Empty, "\u0001", fileNameFilter ?? string.Empty, "\u0001", category.ToString());

    public bool TryReplay(string key, int limit, Action<SearchResult> onResult, CancellationToken token)
    {
        List<SearchResult> page;
        lock (_gate)
        {
            if (!_pages.TryGetValue(key, out var entry))
                return false;

            // Time, not revision, is what retires a page. The desktop changes files constantly, so
            // the revision moved between almost every pair of searches and an exact-match rule left
            // 2 of 4 drives hitting while the other 2 always missed, which put the slowest miss
            // back on the critical path. A page this young is served as-is; older than the window
            // it is rebuilt, which is what lets a just-created file show up a minute later.
            if (Environment.TickCount64 - entry.StoredAt > RecentGraceMs)
                return false;

            // A page shorter than the request is a truncated answer: replaying it would end the stream
            // at its last row and hide every later match. That is how a 204-row quick page used to
            // answer the full window (which asks for int.MaxValue) and stop at 204. Fall through to a
            // real scan instead, which also stores a page big enough for this request.
            //
            // The exception is a page that is short because the scan itself ran out of matches: it
            // already holds everything this key has, so no larger request could be answered better.
            if (entry.Rows.Count < limit && !entry.Exhausted)
                return false;

            Lertaro.Core.SearchIndex.PerfTrace.Mark("idx.cacheHit", Lertaro.Core.SearchIndex.PerfTrace.Begin(), entry.Rows.Count);
            entry.LastUsed = Environment.TickCount64;
            // Copy while the entry is pinned, emit after unlocking: onResult can block on the pipe's
            // bounded channel, and holding the gate across it would serialize every other replay and
            // store behind one slow client. GetRange would throw for an empty page, which is a valid
            // answer here (this drive has no match for the key).
            page = entry.Rows.Count == 0 || entry.Rows.Count == limit ? entry.Rows : entry.Rows.GetRange(0, limit);
        }

        foreach (var row in page)
        {
            token.ThrowIfCancellationRequested();
            onResult(row);
        }

        return true;
    }

    public void Store(string key, List<SearchResult> rows, int scannedLimit)
    {
        // "Everything this key has" -- the scan returned fewer rows than it was allowed to emit, and that
        // allowance was itself small enough that MaxRows truncation below cannot have cut anything off.
        // Such a page answers any request, which is what lets a narrow category hit the cache at all.
        var exhausted = scannedLimit <= MaxRows && rows.Count < scannedLimit;

        // An empty page is stored too, and it is the one that matters most. The cache hangs off each
        // drive's index, so "this drive has nothing for this query" is a real answer that costs a whole
        // index scan to produce and nothing to replay. Skipping it meant a drive with no match for a
        // narrow query -- the usual case for an uncommon letter or filter -- rescanned its entire index
        // on every keystroke while the drives that did match answered from their pages.
        if (rows.Count == 0)
        {
            lock (_gate)
            {
                if (_pages.Count >= Capacity)
                    EvictOldestLocked();
                _pages[key] = new Entry
                {
                    Rows = rows,
                    LastUsed = Environment.TickCount64,
                    StoredAt = Environment.TickCount64,
                    Exhausted = true,
                };
            }

            return;
        }

        // A full-window search asks for int.MaxValue and can return the whole drive; keeping that page
        // would pin an unbounded amount of memory per entry. The prefix stays a valid answer for any
        // request it can cover, because TryReplay refuses a page shorter than the request.
        if (rows.Count > MaxRows)
            rows = rows.GetRange(0, MaxRows);

        lock (_gate)
        {
            // Deliberately no _pages.Clear() here: a mutation of this index does not invalidate the
            // other entries (the age check already bounds them), and clearing on every refresh would
            // throw away pages that had just been stored.
            if (_pages.Count >= Capacity)
                EvictOldestLocked();

            _pages[key] = new Entry
            {
                Rows = rows,
                LastUsed = Environment.TickCount64,
                StoredAt = Environment.TickCount64,
                Exhausted = exhausted,
            };
        }
    }

    // Test seam: the allocation tests measure a cold scan on both sides of a comparison, and a page
    // stored by the warm-up call would answer one side with a replay instead of that scan.
    internal void Clear()
    {
        lock (_gate)
            _pages.Clear();
    }

    /// <summary>One page as it travels to and from disk: the key it answers, its rows, and whether the scan
    /// that produced them had run out of matches. The flag has to survive the round trip -- a restored page
    /// that lost it would be refused forever for being shorter than the request, which is exactly the trap
    /// the flag exists to escape.</summary>
    internal readonly record struct SavedPage(string Key, List<SearchResult> Rows, bool Exhausted);

    /// <summary>Copies the pages out for persistence. The row lists are shared, not cloned.</summary>
    internal List<SavedPage> Snapshot()
    {
        lock (_gate)
        {
            var copy = new List<SavedPage>(_pages.Count);
            foreach (var pair in _pages)
                copy.Add(new SavedPage(pair.Key, pair.Value.Rows, pair.Value.Exhausted));
            return copy;
        }
    }

    /// <summary>Installs pages restored from disk. StoredAt is reset to now, so a restored page gets a full
    /// grace window rather than inheriting the age it had when the process last ran.</summary>
    internal void Import(IEnumerable<SavedPage> pages)
    {
        lock (_gate)
        {
            foreach (var saved in pages)
            {
                if (_pages.Count >= Capacity)
                    EvictOldestLocked();
                _pages[saved.Key] = new Entry
                {
                    Rows = saved.Rows,
                    LastUsed = Environment.TickCount64,
                    StoredAt = Environment.TickCount64,
                    Exhausted = saved.Exhausted,
                };
            }
        }
    }

    private void EvictOldestLocked()
    {
        string? oldestKey = null;
        var oldest = long.MaxValue;
        foreach (var pair in _pages)
        {
            if (pair.Value.LastUsed >= oldest)
                continue;
            oldest = pair.Value.LastUsed;
            oldestKey = pair.Key;
        }

        if (oldestKey != null)
            _pages.Remove(oldestKey);
    }
}
