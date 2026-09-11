using System.IO;
using Lertaro.App.Services.Update;

namespace Lertaro.App.Tests.Services.Update;

// UpdateSourceSettings decides where updates are fetched from and which key is trusted to sign them. It
// reads a file that lives next to the exe rather than in the repository, so a fork can repoint updates
// without that value ever being committed -- which makes the fallbacks the important part: for everyone
// without that file, every branch here has to reproduce the upstream default exactly.
[TestClass]
public sealed class UpdateSourceSettingsTests
{
    [TestMethod]
    public void NoJson_FallsBackToUpstreamDefaults()
    {
        var resolved = UpdateSourceSettings.Parse(null);

        Assert.AreEqual(UpdateSourceSettings.UpstreamApiUrl, resolved.ApiUrl);
        Assert.AreEqual(UpdateSourceSettings.UpstreamPublicKeyPem, resolved.PublicKeyPem);
        Assert.IsFalse(resolved.SkipSignatureVerification);
    }

    [TestMethod]
    public void EmptyJson_FallsBackToUpstreamDefaults()
    {
        var resolved = UpdateSourceSettings.Parse("   ");

        Assert.AreEqual(UpdateSourceSettings.UpstreamApiUrl, resolved.ApiUrl);
        Assert.AreEqual(UpdateSourceSettings.UpstreamPublicKeyPem, resolved.PublicKeyPem);
    }

    [TestMethod]
    public void ApiUrlOverride_IsHonoured()
    {
        const string forkUrl = "https://api.github.com/repos/bobo198504/Lertaro/releases/latest";

        var resolved = UpdateSourceSettings.Parse("""{ "apiUrl": "https://api.github.com/repos/bobo198504/Lertaro/releases/latest" }""");

        Assert.AreEqual(forkUrl, resolved.ApiUrl);
        // Unrelated fields still fall back, so a minimal file cannot silently weaken trust.
        Assert.AreEqual(UpdateSourceSettings.UpstreamPublicKeyPem, resolved.PublicKeyPem);
        Assert.IsFalse(resolved.SkipSignatureVerification);
    }

    [TestMethod]
    public void PublicKeyOverride_IsHonoured() =>
        Assert.AreEqual("-----BEGIN PUBLIC KEY-----\ncustom\n-----END PUBLIC KEY-----",
            UpdateSourceSettings.Parse("""{ "publicKeyPem": "-----BEGIN PUBLIC KEY-----\ncustom\n-----END PUBLIC KEY-----" }""").PublicKeyPem);

    [TestMethod]
    public void RelativeApiUrl_IsRejectedInFavourOfUpstream() =>
        // This value decides where binaries are downloaded from, so a typo must not silently point the
        // updater at nothing (or somewhere unintended).
        Assert.AreEqual(UpdateSourceSettings.UpstreamApiUrl,
            UpdateSourceSettings.Parse("""{ "apiUrl": "repos/foo/bar/releases/latest" }""").ApiUrl);

    [TestMethod]
    public void NonHttpApiUrl_IsRejectedInFavourOfUpstream() =>
        Assert.AreEqual(UpdateSourceSettings.UpstreamApiUrl,
            UpdateSourceSettings.Parse("""{ "apiUrl": "file:///C:/temp/evil" }""").ApiUrl);

    [TestMethod]
    public void BlankPublicKey_StaysOnTheUpstreamKey() =>
        // Disabling trust has to be the explicit boolean; no blank value may achieve it by accident.
        Assert.AreEqual(UpdateSourceSettings.UpstreamPublicKeyPem,
            UpdateSourceSettings.Parse("""{ "publicKeyPem": "   " }""").PublicKeyPem);

    [TestMethod]
    public void SkipSignatureVerification_RequiresAnExplicitTrue() =>
        Assert.IsTrue(UpdateSourceSettings.Parse("""{ "skipSignatureVerification": true }""").SkipSignatureVerification);

    [TestMethod]
    public void MalformedJson_FallsBackToUpstreamDefaults()
    {
        var resolved = UpdateSourceSettings.Parse("{ this is not json");

        Assert.AreEqual(UpdateSourceSettings.UpstreamApiUrl, resolved.ApiUrl);
        Assert.AreEqual(UpdateSourceSettings.UpstreamPublicKeyPem, resolved.PublicKeyPem);
    }

    [TestMethod]
    public void PropertyNamesAreCaseInsensitive() =>
        // Hand-written config is the expected input, so casing should not matter.
        Assert.IsTrue(UpdateSourceSettings.Parse("""{ "SKIPSIGNATUREVERIFICATION": true }""").SkipSignatureVerification);

    [TestMethod]
    public void CommentsAndTrailingCommas_AreTolerated() =>
        // JSON has no comments, but this file is meant to be hand-edited; a trailing comma, an
        // explanatory line, or a bare "//" separator should not drop the whole file back to upstream.
        Assert.AreEqual("https://api.github.com/repos/me/Lertaro/releases/latest",
            UpdateSourceSettings.Parse("""
                {
                  // my fork's release feed
                  //
                  "apiUrl": "https://api.github.com/repos/me/Lertaro/releases/latest",

                  // this fork does not sign its releases
                  "skipSignatureVerification": true
                }
                """).ApiUrl);

    [TestMethod]
    public void MissingFile_ResolvesToDefaults()
    {
        // The real entry point for everyone who never creates the file.
        var missing = Path.Combine(Path.GetTempPath(), "lertaro-does-not-exist", "update-source.json");

        var resolved = UpdateSourceSettings.Load(missing);

        Assert.AreEqual(UpdateSourceSettings.UpstreamApiUrl, resolved.ApiUrl);
        Assert.AreEqual(UpdateSourceSettings.UpstreamPublicKeyPem, resolved.PublicKeyPem);
        Assert.IsFalse(resolved.SkipSignatureVerification);
    }

    [TestMethod]
    public void UpstreamPublicKey_IsAParsableP256Key()
    {
        // The default key is a hand-copied PEM, and nothing else in the suite would notice if a
        // character were dropped or added -- every other assertion compares it against itself. A
        // single stray character makes the base64 body undecodable, ImportFromPem throws, and every
        // genuine upstream release then fails verification with "no supported key formats" even
        // though the download itself succeeded. So parse it for real, the same way the installer does.
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();

        ecdsa.ImportFromPem(UpdateSourceSettings.UpstreamPublicKeyPem);

        Assert.AreEqual(256, ecdsa.KeySize);
    }
}
