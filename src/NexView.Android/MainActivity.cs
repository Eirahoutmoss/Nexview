using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Avalonia;
using Avalonia.Android;
using NexView.Core.Conversion;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;
using NexView.Droid.Codecs;
using NexView.Droid.Platform;
using NexView.Mobile;
using NexView.Mobile.Services;
using IOPath = System.IO.Path;
using Uri = Android.Net.Uri;

namespace NexView.Droid;

[Activity(
    Label = "NexView",
    Theme = "@style/NexViewTheme",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "image/*")]
[IntentFilter([Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "image/*")]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataMimeType = "image/*")]
public class MainActivity : AvaloniaMainActivity<MobileApp>
{
    private AndroidPlatform? _platform;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        _platform = AndroidPlatform.Instance;
        _platform.Activity = this;
        var platform = _platform;
        MobileServices.Factory = () =>
        {
            var codecs = new CodecManager();
            codecs.Register(new AndroidCodec());
            return new MobileServices
            {
                Platform = platform,
                Codecs = codecs,
                Converter = new AndroidConverter(),
                Editor = new AndroidEditor(),
#if AFOT_EDITION
                EditionLabel = NexView.Core.Localization.L.T("AFOT (Adıyaman Fotoğraf Topluluğu) için geliştirilmiştir."),
#endif
            };
        };
        base.OnCreate(savedInstanceState);
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        _platform?.OnActivityResult(requestCode, resultCode);
    }

    /// <summary>Paylaş / Birlikte aç ile gelen görüntüleri önbelleğe kopyalayıp uygulamaya iletir.</summary>
    private void HandleIntent(Intent? intent)
    {
        if (intent == null) return;
        var uris = new List<Uri>();
        try
        {
            switch (intent.Action)
            {
                case Intent.ActionSend:
                    if (GetStream(intent) is { } one) uris.Add(one);
                    break;
                case Intent.ActionSendMultiple:
                    uris.AddRange(GetStreams(intent));
                    break;
                case Intent.ActionView:
                    if (intent.Data is { } data) uris.Add(data);
                    break;
            }
        }
        catch (Exception ex) { Log.Warn("Paylaşım niyeti okunamadı", ex); }
        if (uris.Count == 0) return;
        intent.SetAction(Intent.ActionMain); // yeniden oluşturulunca tekrar işlenmesin

        var resolver = ContentResolver!;
        Task.Run(() =>
        {
            var dir = Directory.CreateDirectory(IOPath.Combine(CacheDir!.AbsolutePath, "session")).FullName;
            var files = new List<string>();
            foreach (var uri in uris)
            {
                try
                {
                    var name = OutputPaths.SanitizeFileName(DisplayName(resolver, uri) ?? $"image_{DateTime.Now:HHmmss}.jpg");
                    var dest = OutputPaths.Unique(IOPath.Combine(dir, name));
                    using (var input = resolver.OpenInputStream(uri) ?? throw new IOException("OpenInputStream"))
                    using (var output = File.Create(dest))
                        input.CopyTo(output);
                    files.Add(dest);
                }
                catch (Exception ex) { Log.Warn($"Paylaşılan dosya alınamadı: {uri}", ex); }
            }
            if (files.Count > 0) MobileServices.Receive(files);
        });
    }

#pragma warning disable CA1422 // Eski API'ler Android 13 öncesi için gerekli
    private static Uri? GetStream(Intent intent) =>
        Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            ? intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Uri))) as Uri
            : intent.GetParcelableExtra(Intent.ExtraStream) as Uri;

    private static IEnumerable<Uri> GetStreams(Intent intent)
    {
        var list = Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            ? intent.GetParcelableArrayListExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Uri)))
            : intent.GetParcelableArrayListExtra(Intent.ExtraStream);
        if (list == null) yield break;
        foreach (var o in list) if (o is Uri u) yield return u;
    }
#pragma warning restore CA1422

    private static string? DisplayName(ContentResolver resolver, Uri uri)
    {
        if (uri.Scheme == "file") return uri.LastPathSegment;
        try
        {
            using var c = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            if (c != null && c.MoveToFirst()) return c.GetString(0);
        }
        catch { }
        return null;
    }
}
