using System.IO;
using System.Text.Json;

namespace Lertaro.App.Services.Update;

/// <summary>
/// Where updates come from, and which key is trusted to sign them.
/// </summary>
/// <remarks>
/// A local override, read from <c>update-source.json</c> next to Lertaro.App.exe. That location matters:
/// for a portable install it is the user's own folder, never the repository, so a fork's update source
/// and trust anchor cannot be committed or offered upstream by accident. The mechanism itself is generic,
/// so upstream merges never have to touch it.
///
/// No file present -- the normal case for everyone -- resolves to exactly the upstream defaults, so
/// behaviour is unchanged. The file is read once and cached; editing it takes a restart.
/// </remarks>
internal sealed record UpdateSourceSettings(string ApiUrl, string PublicKeyPem, bool SkipSignatureVerification)
{
    internal const string UpstreamApiUrl = "https://api.github.com/repos/Lertaro/Lertaro/releases/latest";

    internal const string UpstreamPublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE117370jTbSPgIwHLntC+Bi3SD6gJ\n" +
        "QfxAySjSpUWa6zy4n0YHVv/ZWXM9zQlF2LTqpQC0iHNdJNH+MKU9UvDMTQ==\n" +
        "-----END PUBLIC KEY-----";

    internal static readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-source.json");

    private static readonly Lazy<UpdateSourceSettings> Resolved = new(() => Load(ConfigPath));

    /// <summary>The settings in force for this process.</summary>
    internal static UpdateSourceSettings Current => Resolved.Value;

    internal static UpdateSourceSettings Load(string path)
    {
        if (!File.Exists(path))
            return Defaults();

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            // A broken override must not take the update check down with it; falling back to upstream is
            // the safe reading, and the log line is how the mistake gets noticed.
            Core.Logger.Log($"[UpdateService] Could not read {path}, using upstream update source: {ex.Message}", Core.LogLevel.Warn);
            return Defaults();
        }
    }

    internal static UpdateSourceSettings Defaults() => new(UpstreamApiUrl, UpstreamPublicKeyPem, SkipSignatureVerification: false);

    /// <summary>
    /// Resolves settings from the override file's JSON. Pure so the precedence and the fallbacks can be
    /// tested without touching disk.
    /// </summary>
    internal static UpdateSourceSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Defaults();

        UpdateSourceOverride? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<UpdateSourceOverride>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException)
        {
            return Defaults();
        }

        if (parsed == null)
            return Defaults();

        return new UpdateSourceSettings(
            ResolveApiUrl(parsed.ApiUrl),
            ResolvePublicKey(parsed.PublicKeyPem),
            parsed.SkipSignatureVerification);
    }

    // Only an absolute http(s) URL is accepted: this value decides where binaries are fetched from, so a
    // typo or a relative path must fall back to the known source rather than quietly break the check.
    private static string ResolveApiUrl(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return UpstreamApiUrl;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? candidate
            : UpstreamApiUrl;
    }

    // An unset or blank key means "use the upstream key", never "trust anything". Turning verification
    // off has to be the explicit boolean below, so no config edit can disable it by omission.
    private static string ResolvePublicKey(string? candidate) =>
        string.IsNullOrWhiteSpace(candidate) ? UpstreamPublicKeyPem : candidate;

    /// <summary>Wire shape of <c>update-source.json</c>. Every field is optional.</summary>
    private sealed class UpdateSourceOverride
    {
        public string? ApiUrl { get; set; }
        public string? PublicKeyPem { get; set; }
        public bool SkipSignatureVerification { get; set; }
    }
}
