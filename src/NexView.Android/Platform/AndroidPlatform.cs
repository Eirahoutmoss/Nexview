using Android.App;
using Android.Content;
using Android.Provider;
using AndroidX.Core.Content;
using NexView.Core.Localization;
using NexView.Mobile.Services;
using IOPath = System.IO.Path;
using Uri = Android.Net.Uri;

namespace NexView.Droid.Platform;

/// <summary>Android hizmetleri: MediaStore'a kaydetme, FileProvider ile paylaşma, kamera, harita/bağlantı açma.</summary>
public sealed class AndroidPlatform : IMobilePlatform
{
    public static AndroidPlatform Instance { get; } = new();
    /// <summary>Etkin aktivite (yeniden oluşturulduğunda güncellenir).</summary>
    public Activity? Activity { get; set; }
    private Activity activity => Activity ?? throw new InvalidOperationException("Etkin aktivite yok.");

    public const string Authority = "com.hasanguler.nexview.fileprovider";
    public const int CameraRequest = 4711;
    private TaskCompletionSource<bool>? _camera;

    public string Name => $"Android {Android.OS.Build.VERSION.Release}";
    public string CacheDir => activity.CacheDir!.AbsolutePath;
    public bool CanCapture => true;

    public Task<string?> SaveToGalleryAsync(string path, string displayName, string mimeType) => Task.Run<string?>(() =>
    {
        var isPdf = mimeType == "application/pdf";
        var relative = (isPdf ? Android.OS.Environment.DirectoryDownloads : Android.OS.Environment.DirectoryPictures) + "/NexView";
        var collection = isPdf ? MediaStore.Downloads.ExternalContentUri! : MediaStore.Images.Media.ExternalContentUri!;
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, displayName);
        values.Put(MediaStore.IMediaColumns.MimeType, mimeType);
        values.Put(MediaStore.IMediaColumns.RelativePath, relative);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        var resolver = activity.ContentResolver!;
        var uri = resolver.Insert(collection, values) ?? throw new IOException(L.T("Galeriye kayıt oluşturulamadı."));
        try
        {
            using (var output = resolver.OpenOutputStream(uri) ?? throw new IOException("OpenOutputStream"))
            using (var input = File.OpenRead(path))
                input.CopyTo(output);
            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, values, null, null);
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
        return (isPdf ? L.T("İndirilenler") : L.T("Resimler")) + "/NexView";
    });

    public Task ShareAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return Task.CompletedTask;
        var uris = paths.Select(p => FileProvider.GetUriForFile(activity, Authority, new Java.IO.File(p))!).ToList();
        var mimes = paths.Select(MimeTypes.For).Distinct().ToList();
        var mime = mimes.Count == 1 ? mimes[0] : mimes.All(m => m.StartsWith("image/")) ? "image/*" : "*/*";
        Intent intent;
        if (uris.Count == 1)
        {
            intent = new Intent(Intent.ActionSend);
            intent.PutExtra(Intent.ExtraStream, uris[0]);
        }
        else
        {
            intent = new Intent(Intent.ActionSendMultiple);
            intent.PutParcelableArrayListExtra(Intent.ExtraStream, uris.Cast<Android.OS.IParcelable>().ToList());
        }
        intent.SetType(mime);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        var clip = ClipData.NewRawUri("", uris[0])!;
        foreach (var u in uris.Skip(1)) clip.AddItem(new ClipData.Item(u));
        intent.ClipData = clip;
        activity.StartActivity(Intent.CreateChooser(intent, L.T("Paylaş")));
        return Task.CompletedTask;
    }

    public async Task<string?> CapturePhotoAsync()
    {
        var dir = Directory.CreateDirectory(IOPath.Combine(CacheDir, "camera")).FullName;
        var path = IOPath.Combine(dir, $"IMG_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
        var uri = FileProvider.GetUriForFile(activity, Authority, new Java.IO.File(path));
        var intent = new Intent(MediaStore.ActionImageCapture);
        intent.PutExtra(MediaStore.ExtraOutput, uri);
        intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);
        _camera = new TaskCompletionSource<bool>();
        activity.StartActivityForResult(intent, CameraRequest);
        var ok = await _camera.Task;
        return ok && File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    public void OnActivityResult(int requestCode, Result resultCode)
    {
        if (requestCode == CameraRequest) _camera?.TrySetResult(resultCode == Result.Ok);
    }

    public Task InstallPackageAsync(string path)
    {
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O && !activity.PackageManager!.CanRequestPackageInstalls())
        {
            // "Bilinmeyen uygulamaları yükle" izni için ayarlar sayfası; kullanıcı izin verip tekrar dener
            var settings = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources, Uri.Parse("package:" + activity.PackageName));
            activity.StartActivity(settings);
            throw new InvalidOperationException(L.T("NexView'e uygulama yükleme izni verin, ardından yeniden deneyin."));
        }
        var uri = FileProvider.GetUriForFile(activity, Authority, new Java.IO.File(path));
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(intent);
        return Task.CompletedTask;
    }

    public void OpenUri(string uri)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, Uri.Parse(uri));
            intent.AddFlags(ActivityFlags.NewTask);
            activity.StartActivity(intent);
        }
        catch (ActivityNotFoundException) { }
    }
}
