using Lertaro.PluginSdk.Abstractions.Plugins;
using Lertaro.PluginSdk.Helpers;
using Lertaro.PluginSdk.Services;
using Lertaro.Plugins.CoreExtensions.Views;

namespace Lertaro.Plugins.CoreExtensions.Providers.InstantAnswers;

public sealed class HostsFileInstantProvider : IInstantResultProvider
{
    private static readonly string[] TriggerWords = ["host", "hosts"];

    public string Name => TranslationService.Get("HostsFile_Name");
    public SearchCategoryFilter ResultCategory => SearchCategoryFilter.Settings;
    public IReadOnlyList<string> QueryTriggerKeywords => TriggerWords;

    public IEnumerable<InstantResultItem> GetInstantResults(string query)
    {
        if (!TriggerWord.TryMatchAny(query, TriggerWords, out _, out _))
            yield break;

        yield return new InstantResultItem
        {
            Title = TranslationService.Get("HostsFile_Title"),
            Description = TranslationService.Get("HostsFile_Description"),
            IconData = "M6 2h8l4 4v16H6V2zm8 1.5V7h3.5L14 3.5zM8 11h8v2H8zm0 4h8v2H8z",
            ActionType = "None",
            TabCompletion = "host",
            OnExecute = HostsEditorWindow.ShowEditor
        };
    }
}
