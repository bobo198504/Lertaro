using System.Windows;

namespace Lertaro.App.Views.SearchWindow;

// Split out purely to keep SearchWindow under the repo's per-file line limit. Owns the full window's
// "hand the query back to the quick window, then close" move: it used to sit behind the back button
// only, but the global summon hotkey means the same thing when this window already holds foreground,
// and one implementation keeps the two routes from drifting apart.
internal static class SearchWindowQuickReturnSupport
{
    internal static void ReturnToQuickSearch(Lertaro.App.SearchWindow window)
    {
        var quickSearchWindow = System.Windows.Application.Current.MainWindow as Lertaro.App.QuickSearchWindow;
        if (quickSearchWindow == null)
        {
            foreach (Window win in System.Windows.Application.Current.Windows)
            {
                if (win is Lertaro.App.QuickSearchWindow qsw)
                {
                    quickSearchWindow = qsw;
                    break;
                }
            }
        }

        if (quickSearchWindow != null)
        {
            // This window's own query wins; the quick window's last one is the fallback for when the
            // full window was opened without any text of its own.
            var query = !string.IsNullOrWhiteSpace(window.SearchText)
                ? window.SearchText
                : quickSearchWindow.ViewModel.SearchQuery;
            var quickAlreadySearchedThisQuery = string.Equals(
                quickSearchWindow.ViewModel.Search.SearchQuery, query, StringComparison.Ordinal);

            // Switching windows is one search replacing another, never two searches competing for the
            // service. Cancel the full window before the quick window starts its own generation.
            window.CancelActiveSearch();

            // caretAtEnd: the query is carried over rather than retyped, so the next keystroke should
            // append to it. Leaving the caret at position 0 put it in front of the text the user already
            // had and made the first key they pressed after coming back land in the wrong place.
            quickSearchWindow.ShowWindow(query, caretAtEnd: true);
            // A same-text assignment is a binding no-op, but the quick window still has to issue the
            // lighter search now that the full window has stopped.
            if (quickAlreadySearchedThisQuery)
            {
                quickSearchWindow.ViewModel.Search.CancelPendingSearch();
                quickSearchWindow.ViewModel.Search.PerformSearch(query);
            }
        }

        window.Close();
    }
}
