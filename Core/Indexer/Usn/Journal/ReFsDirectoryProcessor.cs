using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

using Lertaro.Core.Indexer.NetworkDrive.Walk;
namespace Lertaro.Core.Indexer.Usn.Journal;

// Split out of ReFsScanner purely to keep that file under the repo's per-file line limit: this is the
// per-directory walk that turns one ReFS directory's entries into ReFsItems. It has no state of its own;
// the only thing it borrows from the scanner is CopyReusedChildren, which it calls explicitly.
internal static class ReFsDirectoryProcessor
{
    internal static void ProcessDir(
        SafeFileHandle volumeHandle,
        UInt128 dirId,
        ConcurrentDictionary<UInt128, ReFsItem> items,
        TreeDiffBaseline? diffBaseline,
        ReFsCheckpointState checkpointState,
        Action<int, int>? onProgress,
        ref int files,
        ref int dirs,
        ref int errors,
        Action<UInt128> onSubdir)
    {
        if (diffBaseline != null && items.TryGetValue(dirId, out var self)
            && diffBaseline.TryGetUnchangedChildren(dirId, FileTimeHelper.FileTimeToUnixSeconds(self.LastWriteTimeUtc), out var cachedChildren))
        {
            ReFsScanner.CopyReusedChildren(cachedChildren, dirId, items, ref files, ref dirs, onSubdir, checkpointState);
            items.TryUpdate(dirId, self with { Listed = true }, self);
            onProgress?.Invoke(Volatile.Read(ref files), Volatile.Read(ref dirs));
            return;
        }

        var desc = new Win32Api.FILE_ID_DESCRIPTOR
        {
            dwSize = 24,
            Type = 2, // ExtendedFileIdType
            ExtendedFileId = new Win32Api.FILE_ID_128 { Low = (ulong)dirId, High = (ulong)(dirId >> 64) }
        };
        using var dirHandle = Win32Api.OpenFileById(volumeHandle, ref desc,
            1, Win32Api.FILE_SHARE_READ | Win32Api.FILE_SHARE_WRITE | 4,
            IntPtr.Zero, Win32Api.FILE_FLAG_BACKUP_SEMANTICS);
        if (dirHandle.IsInvalid)
        {
            // Same "count it, leave the directory un-Listed, move on" shape as
            // TreeBuilder.WalkDirectory's own CountError(ref _enumerateErrors) -- this directory (deleted
            // out from under the scan, permission-denied, etc.) never reaches the Listed marking below, so
            // a future rebuild retries it; this counter only feeds the completion-time diagnostic log.
            Interlocked.Increment(ref errors);
            return;
        }

        const int bufSize = 1024 * 1024;
        var buf = Marshal.AllocHGlobal(bufSize);
        var enumerationFailed = false;
        try
        {
            // Loop until GetFileInformationByHandleEx returns false. A false return is only a normal
            // end-of-directory when the last Win32 error is ERROR_NO_MORE_FILES -- any other code (access
            // denied, directory deleted mid-scan, etc.) is a real failure partway through enumeration, so
            // the entries already added to `items` below may be an incomplete listing of this directory.
            // The original code only called it once, missing entries in large directories.
            while (Win32Api.GetFileInformationByHandleEx(dirHandle, Win32Api.FileIdExtdDirectoryInfo, buf, bufSize))
            {
                var cur = buf;
                while (true)
                {
                    var nextOff = (uint)Marshal.ReadInt32(cur, 0);
                    // FILE_ID_EXTD_DIR_INFO: already-fetched fields, no extra I/O to read them.
                    var creationTimeUtc = Marshal.ReadInt64(cur, 8);
                    var lastAccessTimeUtc = Marshal.ReadInt64(cur, 16);
                    var lastWriteTimeUtc = Marshal.ReadInt64(cur, 24);
                    var size = Marshal.ReadInt64(cur, 40);
                    var attrs = (uint)Marshal.ReadInt32(cur, 56);
                    var nameLen = (uint)Marshal.ReadInt32(cur, 60);
                    var idLow = (ulong)Marshal.ReadInt64(cur, 72);
                    var idHigh = (ulong)Marshal.ReadInt64(cur, 80);
                    var fileId = new UInt128(idHigh, idLow);
                    var name = Marshal.PtrToStringUni(cur + 88, (int)nameLen / 2);
                    if (name != "." && name != "..")
                    {
                        var isDir = (attrs & 0x10) != 0;
                        var item = new ReFsItem(name!, dirId, isDir, isDir ? 0 : size, creationTimeUtc, lastWriteTimeUtc, lastAccessTimeUtc);
                        if (items.TryAdd(fileId, item))
                        {
                            if (isDir)
                            {
                                Interlocked.Increment(ref dirs);
                                onSubdir(fileId);
                            }
                            else
                            {
                                Interlocked.Increment(ref files);
                            }

                            // Throttled on the counters maintained just above. items.Count was the obvious
                            // thing to reach for, but ConcurrentDictionary.Count takes a lock on every
                            // stripe and sums them -- in the per-entry loop of a scan adding millions of
                            // entries from 8 concurrent workers, that briefly serialized all of them every
                            // 4096th add. The callback is advisory progress, so the same looseness about
                            // which worker happens to see the boundary is fine either way.
                            if (((Volatile.Read(ref files) + Volatile.Read(ref dirs)) & 4095) == 0)
                                onProgress?.Invoke(Volatile.Read(ref files), Volatile.Read(ref dirs));

                            checkpointState.MaybeCheckpoint(items);
                        }
                    }
                    if (nextOff == 0) break;
                    cur += (int)nextOff;
                }
            }

            // Captured immediately after the loop exits, before FreeHGlobal or anything else can touch
            // the thread's last-error slot -- ERROR_NO_MORE_FILES is the only "this was a normal end of
            // directory" code; anything else means the entries already added above are a partial listing.
            if (Marshal.GetLastWin32Error() != Win32Api.ERROR_NO_MORE_FILES)
            {
                enumerationFailed = true;
                Interlocked.Increment(ref errors);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        // dirId's own entry (if it has one -- the root doesn't) was added by whichever parent discovered
        // it, with Listed defaulted to false; now that its own children are fully gathered, mark it the
        // same way a reused directory is marked above, so a LATER scan's diff baseline can trust IT too.
        // Skipped on a real enumeration failure (see above) -- this directory's own listing may be
        // incomplete, so it must stay un-Listed for a future rebuild to retry, same as the
        // dirHandle.IsInvalid case above.
        if (!enumerationFailed && items.TryGetValue(dirId, out var listedSelf))
            items.TryUpdate(dirId, listedSelf with { Listed = true }, listedSelf);
    }

    // Copies a reused directory's cached children into `items`, recursing into cached subdirectories for
    // their OWN reuse check via onSubdir (never trusting a subtree more than one level deep at a time).
    // Silently skips a cached child whose id already exists in `items` -- a corrupted/duplicated baseline
    // (e.g. a duplicate row left by some earlier bug) -- rather than aborting the whole reuse: this
    // directory's other, non-colliding cached children are still perfectly valid to reuse, and skipping
    // (not re-adding) a duplicate is always safe, the same conservative choice TreeBuilder's own
    // EnqueueDirectory makes for the equivalent case.
}
