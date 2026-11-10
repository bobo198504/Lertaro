using System.Text;
using Lertaro.Core.SearchIndex.Fzf;

namespace Lertaro.Core.Tests.SearchIndex.Fzf;

[TestClass]
public sealed class FzfSingleCharacterFastPathTests
{
    private static readonly string[] Texts =
    [
        "a", "A", "apex", "Apex.exe", "x-a", "x_a", "x a", "xa", "ax", "a---a",
        "foo/a/bar", "fooAbar", "123a", "a123", ".a", "a.", "中a文", "éa", "a中", "zzz",
    ];

    private static readonly char[] Patterns = ['a', 'A', 'z', '1', '.', '/', '中', 'é'];

    [TestMethod]
    public void CharacterFastPathMatchesTheFullV2SingleCharacterAlgorithm()
    {
        foreach (var text in Texts)
        foreach (var pattern in Patterns)
        foreach (var scheme in Enum.GetValues<FzfScoringScheme>())
        foreach (var caseSensitive in new[] { false, true })
        {
            var expected = ReferenceChar(text, pattern, caseSensitive, scheme);
            var actual = FzfFuzzyMatcher.FuzzyMatchV2(
                text, pattern.ToString(), caseSensitive, scheme);

            Assert.AreEqual(expected, actual, $"{text} / {pattern} / {scheme} / case={caseSensitive}");
        }
    }

    [TestMethod]
    public void ByteFastPathMatchesTheFullV2SingleCharacterAlgorithm()
    {
        foreach (var text in Texts.Where(text => Ascii.IsValid(text)))
        foreach (var pattern in Patterns.Where(c => c < 128))
        foreach (var scheme in Enum.GetValues<FzfScoringScheme>())
        foreach (var caseSensitive in new[] { false, true })
        {
            var expectedChars = ReferenceChar(text, pattern, caseSensitive, scheme);
            var textBytes = Encoding.ASCII.GetBytes(text);
            var actualBytes = FzfByteMatcher.FuzzyMatchV2(
                textBytes, [(byte)pattern], caseSensitive, scheme, new FzfSlab(), new FzfByteBuffers());

            Assert.AreEqual(expectedChars, actualBytes, $"{text} / {pattern} / {scheme} / case={caseSensitive}");
        }
    }

    [TestMethod]
    public void FastPathStillPrefersTheLeftEdgeBoundary()
    {
        var match = FzfFuzzyMatcher.FuzzyMatchV2(
            "x---a-more-a", "a", caseSensitive: false, FzfScoringScheme.Default);

        Assert.AreEqual(4, match.Start);
        Assert.AreEqual(5, match.End);
    }

    [TestMethod]
    public void ContiguousPrefixUsesTheExactPrefixScore()
    {
        var match = FzfFuzzyMatcher.FuzzyMatchV2(
            "Apex.exe", "ape", caseSensitive: false, FzfScoringScheme.Default);
        var prefix = FzfExactMatcher.PrefixMatch(
            "Apex.exe", "ape", caseSensitive: false, FzfScoringScheme.Default);

        Assert.AreEqual(prefix, match);
        Assert.AreEqual(0, match.Start);
        Assert.AreEqual(3, match.End);
    }

    [TestMethod]
    public void ContiguousPrefixMatchesTheBytePath()
    {
        var text = Encoding.ASCII.GetBytes("Apex.exe");
        var pattern = Encoding.ASCII.GetBytes("ape");

        var match = FzfByteMatcher.FuzzyMatchV2(
            text, pattern, caseSensitive: false, FzfScoringScheme.Default,
            new FzfSlab(), new FzfByteBuffers());
        var prefix = FzfByteExactMatcher.PrefixMatch(
            text, pattern, caseSensitive: false, FzfScoringScheme.Default);

        Assert.AreEqual(prefix, match);
    }

    [TestMethod]
    public void PrefixFastPathMatchesTheFullV2Algorithm()
    {
        string[] texts = ["apex", "Apex.exe", "app-report", "application", "a---pe", "ape ape"];
        string[] patterns = ["ap", "ape", "app", "Appl", "a-"];

        foreach (var text in texts)
        foreach (var pattern in patterns)
        foreach (var scheme in Enum.GetValues<FzfScoringScheme>())
        foreach (var caseSensitive in new[] { false, true })
        {
            var expected = FzfFuzzyMatcher.FuzzyMatchV2(
                text, pattern, caseSensitive, scheme, enableFastPaths: false);
            var actual = FzfFuzzyMatcher.FuzzyMatchV2(
                text, pattern, caseSensitive, scheme);

            Assert.AreEqual(expected, actual, $"{text} / {pattern} / {scheme} / case={caseSensitive}");
        }
    }

    [TestMethod]
    public void BytePrefixFastPathMatchesTheFullV2Algorithm()
    {
        string[] texts = ["apex", "Apex.exe", "app-report", "application", "a---pe", "ape ape"];
        string[] patterns = ["ap", "ape", "app", "Appl"];

        foreach (var text in texts)
        foreach (var pattern in patterns)
        foreach (var scheme in Enum.GetValues<FzfScoringScheme>())
        foreach (var caseSensitive in new[] { false, true })
        {
            var textBytes = Encoding.ASCII.GetBytes(text);
            var patternBytes = Encoding.ASCII.GetBytes(pattern);
            var expected = FzfByteMatcher.FuzzyMatchV2(
                textBytes, patternBytes, caseSensitive, scheme,
                new FzfSlab(), new FzfByteBuffers(), enableFastPaths: false);
            var actual = FzfByteMatcher.FuzzyMatchV2(
                textBytes, patternBytes, caseSensitive, scheme,
                new FzfSlab(), new FzfByteBuffers());

            Assert.AreEqual(expected, actual, $"{text} / {pattern} / {scheme} / case={caseSensitive}");
        }
    }

    private static FzfMatchResult ReferenceChar(
        string text, char pattern, bool caseSensitive, FzfScoringScheme scheme)
    {
        var patternText = pattern.ToString();
        if (!FzfScoring.FindFuzzyScope(
                text, patternText, caseSensitive, out var minIdx, out var maxIdx))
            return FzfMatchResult.NoMatch;

        var scopedLength = maxIdx - minIdx;
        var chars = new char[scopedLength];
        var bonus = new short[scopedLength];
        var previousClass = (byte)FzfAlgorithm.InitialClass(scheme);
        for (var offset = 0; offset < scopedLength; offset++)
        {
            var raw = text[minIdx + offset];
            var currentClass = FzfCharTables.GetClass(raw);
            chars[offset] = caseSensitive ? raw : FzfCharTables.ToLower(raw);
            bonus[offset] = FzfCharTables.Bonus(scheme, previousClass, currentClass);
            previousClass = currentClass;
        }

        var bestScore = 0;
        var bestPos = -1;
        for (var i = 0; i < scopedLength; i++)
        {
            if (!FzfCharTables.CharsEqual(chars[i], pattern, caseSensitive))
                continue;
            var score = FzfAlgorithm.ScoreMatch + bonus[i] * FzfAlgorithm.BonusFirstCharMultiplier;
            if (score <= bestScore)
                continue;
            bestScore = score;
            bestPos = i;
            if (bonus[i] >= FzfAlgorithm.BonusBoundary)
                break;
        }

        return bestPos >= 0
            ? new FzfMatchResult(minIdx + bestPos, minIdx + bestPos + 1, bestScore)
            : FzfMatchResult.NoMatch;
    }
}
