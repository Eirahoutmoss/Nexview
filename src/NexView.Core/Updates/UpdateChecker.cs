using System.Net.Http.Headers;
using System.Text.Json;

namespace NexView.Core.Updates;

public sealed record UpdateAsset(string Name, string Url, long Size);
public sealed record UpdateInfo(Version Version, string Tag, string Title, string Notes, string PageUrl, IReadOnlyList<UpdateAsset> Assets)
{
    /// <summary>Adında verilen parçaların hepsi geçen ilk dosya (ör. "Setup", ".exe").</summary>
    public UpdateAsset? Find(params string[] parts) =>
        Assets.FirstOrDefault(a => parts.All(p => a.Name.Contains(p, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// GitHub'daki son sürümü (releases/latest) denetler ve dosyayı indirir. Yalnızca kullanıcı istediğinde çalışır;
/// sürüm bilgisi dışında hiçbir veri gönderilmez.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "Eirahoutmoss/Nexview";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NexView", "1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Yayımlanmış son sürüm; mevcut sürümden yeni değilse null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // henüz sürüm yok
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParse(tag, out var v)) return null;
        var assets = root.TryGetProperty("assets", out var arr)
            ? arr.EnumerateArray().Select(a => new UpdateAsset(
                a.GetProperty("name").GetString() ?? "",
                a.GetProperty("browser_download_url").GetString() ?? "",
                a.TryGetProperty("size", out var s) ? s.GetInt64() : 0)).ToList()
            : new List<UpdateAsset>();
        var info = new UpdateInfo(v, tag,
            root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage,
            assets);
        return Normalize(v) > Normalize(current) ? info : null;
    }

    public static bool TryParse(string tag, out Version version)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+', ' ']);
        if (dash > 0) t = t[..dash];
        return Version.TryParse(t, out version!);
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>Dosyayı geçici klasöre indirir; ilerleme 0..1.</summary>
    public static async Task<string> DownloadAsync(UpdateAsset asset, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, asset.Name);
        var tmp = path + ".part";
        using (var resp = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(done / (double)total);
            }
        }
        File.Move(tmp, path, true);
        return path;
    }
}
