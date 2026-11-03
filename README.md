# NexView

**Görüntülerin tüm dünyası.** — Aç. İncele. Düzenle. Dönüştür.

NexView, Nex ailesinin görüntü uygulamasıdır: geniş format destekli, hızlı, yerel çalışan (local-first) bir görüntüleyici, inceleyici ve dönüştürücü. Kullanıcının dosyanın formatını bilmesi gerekmez.

## İndir

Son sürüm: [Releases](https://github.com/Eirahoutmoss/Nexview/releases) — Windows kurulum (`NexView-<sürüm>-Setup.exe`), taşınabilir zip ve Android APK.
Uygulama içinden **Ayarlar → Hakkında → Güncellemeleri denetle** ile yeni sürüm indirilip kurulabilir.

## Yenilikler (v0.7)

- **NexView Stüdyo** (araç çubuğundaki katman simgesi): Makro odak birleştirme, HDR, Panorama ve Astro tek pencerede. Kare şeridi, EXIF tabanlı ön denetim, hızlı ve **canlı önizleme**, önce/sonra karşılaştırma, 16 bit çıktı.
  - Makro: ECC hizalama (odak kaynaklı büyüme dahil), Karma / Ayrıntı / Pürüzsüz yöntemler, hale bastırma.
  - HDR: pozlamadan bağımsız hizalama, hayalet giderme, Doğal / Canlı / Dramatik görünüm.
  - Panorama: OpenCV Stitcher (küresel / düz yüzey), ufuk düzeltme, pozlama telafisi, otomatik kırpma, küçük gezegen ve tünel çıktısı, GPano 360° bilgisi.
- **360° görüntüleyici:** küresel panoramaların içinde sürükleyerek gezinme; kısmi panoramalarda görüş, kapsanan alanla sınırlı.
- **Android uygulaması:** galeri, dokunmatik görüntüleyici, bilgi/GPS, dönüştürme ve hazır ayarlar, PDF, düzenleyici (ton, kırpma, işaretleme, gizleme), belge tarama.
- **Uygulama içi güncelleme** (masaüstü ve Android).

## Durum

| Alan | Durum |
|---|---|
| Görüntüleyici: sığdır, %100, imleç merkezli zoom, kaydırma, döndürme, çevirme, tam ekran, arka planlar | ✓ |
| Film şeridi (sanallaştırılmış, disk önbellekli), sürükle-bırak, klasör tarama | ✓ |
| İçerik imzasıyla format algılama, uzantı uyuşmazlığı uyarısı, "Neden açılmıyor?" ekranı | ✓ |
| RAW: önce gömülü önizleme, ardından tam çözümleme | ✓ |
| Bilgi paneli: dosya, kamera, konum + GPS gizlilik uyarısı, teknik bilgi, ham metadata | ✓ |
| **Dönüştürme:** tekli ve toplu, hazır ayarlar (Paylaşmaya hazırla, Kurum/Resmi belge, Web, Sosyal, E-posta, Baskı, Arşiv) | ✓ |
| Hedef dosya boyutu ("en fazla N KB"), yeniden boyutlandırma, metadata politikası (koru / GPS sil / tümünü sil) | ✓ |
| Görüntüleri tek PDF'te birleştirme (A4 ya da görüntü boyutu, hedef boyut destekli) | ✓ |
| Toplu işlem: duraklat / devam / iptal, kalan süre, hata raporu; hatalı dosya kuyruğu durdurmaz | ✓ |
| Güvenli yazma: geçici dosya → doğrulama → taşıma; orijinal hiçbir zaman değiştirilmez | ✓ |
| Tek pencere (ikinci açılış mevcut pencereye iletilir) | ✓ |
| Pano: görüntü kopyala (Ctrl+C), panodan yapıştır ve kaydet (Ctrl+V) — Windows | ✓ |
| Geri Dönüşüm Kutusu'na silme, yeniden adlandırma (F2), yazdırma (Ctrl+P) — Windows | ✓ |
| Animasyonlu GIF / WebP / APNG oynatma | ✓ |
| FITS (astronomi) + otomatik germe (yalnızca görünüm) | ✓ |
| Türkçe / İngilizce arayüz (Ayarlar → Dil) | ✓ |
| **Düzenleme (tahribatsız):** pozlama, parlaklık, kontrast, doygunluk, sıcaklık, ton, keskinlik, siyah-beyaz, otomatik seviye | ✓ |
| Kırp (serbest, 1:1, 4:3, 3:2, 16:9, A4, biyometrik 50×60), 90° döndür, çevir, ufuk düzeltme | ✓ |
| İşaretleme: ok, kutu, elips, metin, vurgu; gizleme: bulanıklaştır, pikselleştir, siyah kutu (piksellere işlenir) | ✓ |
| Belge modu: perspektif düzeltme (4 köşe) + iyileştir / gri / siyah-beyaz tarama filtresi | ✓ |
| Önce / bölünmüş karşılaştırma, geri al / yinele, yeni dosyaya kaydet (orijinal korunur) | ✓ |
| **Windows entegrasyonu:** "Birlikte aç" ve Varsayılan Uygulamalar kaydı, Explorer sağ tık alt menüsü (aç, JPEG/PNG/WebP'ye dönüştür, paylaşmaya hazırla, dönüştür…, dosya bilgisi) | ✓ |
| **Astro stack:** yıldız izi, ortalama, medyan, sigma kırpma; yıldız eşleştirmeli otomatik hizalama (dönme + öteleme); 16 bit TIFF/PNG çıktı | ✓ |
| Komşu görüntülerin arka planda ön yüklenmesi (anında geçiş), oturumu geri yükleme, kısayol ekranı (F1), tanılama raporu, ekran okuyucu adları | ✓ |
| Kurulum (Inno Setup, yönetici izni gerektirmez, kaldırınca iz bırakmaz) ve taşınabilir paket | ✓ |
| Basit stack (yıldız izi, gürültü azaltma), Explorer küçük resim sağlayıcısı | Sonraki fazlar |

## Desteklenen formatlar (okuma)

JPEG, PNG, GIF, BMP, TIFF, WebP, AVIF, HEIC/HEIF, ICO, TGA, DDS, PSD/PSB, JPEG XL, JPEG 2000, OpenEXR, FITS
RAW: DNG, CR2, CR3, NEF/NRW, ARW/SRF/SR2, RAF, ORF, RW2, SRW, PEF

Gerçek destek çalışma anında codec'in bildirdiği kapasiteden hesaplanır (Bilgi → Teknik Bilgiler → Format desteği). Desteklenmeyen bir format için sahte başarı gösterilmez.

## Teknoloji seçimi ve gerekçesi

| Katman | Seçim | Neden |
|---|---|---|
| Dil / çalışma zamanı | C# / .NET 10 | Yüksek performans, olgun araçlar, Windows/Linux/macOS desteği |
| Arayüz | Avalonia UI 11 | Native çizim (Skia), GPU hızlandırma; aynı kod Windows, Linux, macOS ve iOS/Android'de çalışabilir |
| Codec | Magick.NET (ImageMagick) | Tek bağımlılıkla libjpeg-turbo, libpng, libwebp, libavif, libheif+libde265 (HEIC), libraw (RAW), libjxl, OpenJPEG, OpenEXR; Apache 2.0 lisansı |
| Stüdyo motoru | OpenCvSharp (OpenCV 4.10) | Panorama dikişi, ECC/ORB hizalama, piramit harmanlama; Apache 2.0 |
| Metadata | MetadataExtractor | EXIF/IPTC/XMP/GPS, saf .NET |
| MVVM | CommunityToolkit.Mvvm | Kaynak üreteçli, hafif |

**Taşınabilirlik ilkesi:** `NexView.Core` arayüzden ve codec kütüphanesinden bağımsızdır. Codec'ler `IImageCodec` arayüzüyle takılır; örneğin iOS'ta Magick.NET yerine platform codec'i kullanan yeni bir eklenti yazılabilir, UI değişmez. İşletim sistemine özgü işlemler `Services/PlatformShell` içinde toplanmıştır.

## Desteklenen formatlar (yazma)

JPEG, PNG, WebP, AVIF, JPEG XL, TIFF, BMP, GIF, ICO (çoklu boyut), JPEG 2000, TGA, PDF (birleştirme). HEIC yazma, kurulu codec izin veriyorsa listelenir.

## Windows entegrasyonu

Tüm kayıtlar kullanıcı düzeyindedir (`HKCU`), yönetici izni gerekmez:

- **Birlikte aç / Varsayılan uygulamalar:** NexView desteklenen uzantılar için `OpenWithProgids` ve `RegisteredApplications` altına eklenir. Windows 10/11 varsayılanı programın değiştirmesine izin vermez; Ayarlar'daki "Varsayılan uygulama olarak seç…" düğmesi doğrudan ilgili Windows ekranını açar.
- **Sağ tık menüsü:** `SystemFileAssociations\.<uzantı>\shell\NexView` altında alt menü. Windows 11'de "Diğer seçenekleri göster" altında görünür.
- **Kurulum:** Ayarlar → "Bilgisayara kur" ya da `--install`; yönetici izni gerekmez, "Uygulamalar ve özellikler"den kaldırılabilir.
- Ayarlar → Windows entegrasyonu bölümünden hangi dosya türlerinin ilişkilendirileceği seçilir; kurulum programı da aynı komutları kullanır.

Komut satırı:

```
NexView.exe [dosya|klasör ...] [--tab info|edit|convert|batch] [--new-window]
NexView.exe --convert jpeg|png|webp|avif|tiff|pdf|share|official|web|social|email|print|archive dosya ...
NexView.exe --register        (seçili dosya türleri + sağ tık menüsü)
NexView.exe --unregister      (tüm kayıtları temizler)
NexView.exe --install         (kullanıcı bazlı kurulum: %LOCALAPPDATA%\Programs\NexView, Başlat menüsü, kaldırma kaydı)
NexView.exe --uninstall [--quiet]
```

## Sürüm paketi

- **Kurulum:** `NexView-<sürüm>-Setup.exe`, NexView'in kendisidir; exe adında "Setup" geçtiğinde (ya da `--setup` ile) kurulum sihirbazı açılır. Yönetici izni gerekmez, `%LOCALAPPDATA%\Programs\NexView` altına kurar; dosya ilişkilendirme, sağ tık menüsü ve masaüstü kısayolu seçilebilir; mevcut kurulumu günceller; "Uygulamalar"dan kaldırılır.
- **Taşınabilir:** zip içindeki exe + `portable.txt`; ayarlar exe yanında `Data` klasöründe.

`tools\build-release.ps1` tek dosyalık exe'yi, taşınabilir zip'i (`portable.txt` ile) ve Inno Setup kuruluyse `installer\NexView.iss` ile kurulum dosyasını `dist\` altına üretir.

### AFOT sürümü

`tools\build-release.ps1 -Edition AFOT` (ya da `dotnet publish ... -p:NexViewEdition=AFOT`) Hakkında bölümünde "AFOT (Adıyaman Fotoğraf Topluluğu) için geliştirilmiştir." ibaresini taşıyan taşınabilir sürümü üretir. Normal sürüm bundan etkilenmez.

## Proje yapısı

```
src/
  NexView.Core/            Format algılama, codec yöneticisi, metadata, önbellek, ayarlar, günlük
  NexView.Codecs.Magick/   ImageMagick codec, dönüştürücü ve düzenleyici + RAW gömülü önizleme çıkarıcı
  NexView.Platform.Windows/ Windows'a özgü: pano, Geri Dönüşüm Kutusu, yazdırma, dosya ilişkilendirme, sağ tık menüsü
  NexView.UI/              Ortak arayüz bileşenleri (görüntüleme yüzeyi, düzenleme kaplaması, 360° görüntüleyici, tema)
  NexView.Studio/          Stüdyo motoru (OpenCV): makro, HDR, panorama
  NexView.Mobile/          Mobil arayüz (Avalonia, tek görünüm)
  NexView.Android/         Android başlığı: platform codec'i, dönüştürücü, düzenleyici, paylaşım
  NexView.App/             Masaüstü uygulaması (Avalonia, MVVM)
installer/                 Inno Setup kurulum betiği
tests/NexView.Core.Tests/  xUnit testleri (format, codec, dönüştürme, düzenleme, yerelleştirme, kayıt defteri)
tools/                     devloop.ps1 (geliştirme döngüsü), make_icons.py (marka setinden ikon), check_i18n.py (çeviri denetimi)
```

## Geliştirme

Gereksinim: .NET 10 SDK.

```powershell
dotnet build NexView.sln
dotnet test NexView.sln
dotnet run --project src/NexView.App
```

Yayın (tek dosya, kurulumsuz):

```powershell
dotnet publish src/NexView.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/win-x64
```

Taşınabilir mod: `NexView.exe` ile aynı klasöre `portable.txt` koyulursa ayarlar, önbellek ve günlükler `Data/` klasöründe tutulur. Aksi halde `%LOCALAPPDATA%\NexView`.

## Klavye kısayolları

| Tuş | İşlev | Tuş | İşlev |
|---|---|---|---|
| Ctrl+O | Görüntü aç | Ctrl+Shift+O | Klasör aç |
| Ctrl+C | Görüntüyü kopyala | Ctrl+V | Panodan yapıştır |
| Ctrl+Shift+C | Yolu kopyala | Ctrl+P | Yazdır |
| C / Ctrl+S | Dönüştür | B | Toplu işlem |
| E | Düzenle | Ctrl+Z / Ctrl+Y | Düzenlemede geri al / yinele |
| F2 | Yeniden adlandır | Delete | Geri Dönüşüm Kutusu'na taşı |
| ← / → , Space | Önceki / sonraki | Home / End | İlk / son |
| F | Sığdır | 1 | %100 |
| + / − | Yakınlaştır / uzaklaştır | R / Shift+R | Sağa / sola döndür |
| H | Yatay çevir | G | Arka plan |
| A | Otomatik germe (astro/HDR) | P | Animasyon oynat/duraklat |
| I | Bilgi paneli | T | Film şeridi |
| S | Favori | F11 / Esc | Tam ekran / çık |
| F1 | Kısayol listesi | | |

Fare: tekerlek imleç merkezli zoom, sürükleyerek kaydırma, çift tık sığdır ↔ %100.

## Astro stack

Toplu İşlem → **Astro stack**. Geçerli listedeki (ör. bir gece çekiminin klasörü) kareler birleştirilir:

- **Yıldız izi:** her pikselin en parlak değeri; hizalama yapılmaz.
- **Ortalama / Medyan / Sigma kırpma:** gürültü azaltma. Kareler parlak yıldızlardan üçgen benzeri çift eşleştirme ve en küçük kareler ile hizalanır (dönme + öteleme). Medyan ve sigma kırpma uçak/uydu izlerini temizler; bellek kullanımını sınırlamak için kareler diske yazılıp şerit şerit işlenir.
- Dark/flat/bias kalibrasyonu yapılmaz; ileri derin uzay işlemleri için Siril gibi araçlar önerilir.

## Yerelleştirme

Kaynak metin Türkçedir ve doğrudan anahtar olarak kullanılır (`L.T("…")`, XAML'de `{l:T '…'}`). Çeviriler `src/NexView.Core/Localization/<dil>.json` dosyalarındadır. Yeni dil için `en.json` kopyalanıp çevrilir ve `L.Available` listesine eklenir. `python tools/check_i18n.py` eksik çevirileri listeler.

## Android

```powershell
powershell -ExecutionPolicy Bypass -File tools\setup-android.ps1   # bir kez: .NET Android iş yükü, SDK, JDK
dotnet publish src\NexView.Android\NexView.Android.csproj -c Release -f net10.0-android -o dist\android
```

## Gizlilik

Tüm görüntüleme, düzenleme ve birleştirme yereldir; görüntüler hiçbir yere gönderilmez. İnternete yalnızca kullanıcının açıkça tıkladığı "Haritada aç" ve "Güncellemeleri denetle" düğmeleri çıkar (ikincisi yalnızca GitHub'dan sürüm bilgisini alır).

## Bilinen sınırlamalar

- Ekran için gösterilen görüntü uzun kenarda 8192 pikselle sınırlıdır (bilgi panelinde gerçek çözünürlük gösterilir). Döşemeli (tiled) çözümleme sonraki fazda.
- Animasyonlar en fazla 2048 piksel ve toplam 400 MB'a kadar belleğe alınır; daha büyükleri ilk kareyle gösterilir.
- Pano görüntüsü, Geri Dönüşüm Kutusu ve yazdırma şimdilik yalnızca Windows'ta.
- Renk yönetimi: gömülü ICC profili sRGB'ye dönüştürülür; monitör profili henüz uygulanmaz.

## Yol haritası

Faz 3–5 tamamlama (format testleri, IPTC/XMP ayrıntıları) → Faz 6 Dönüştürme ve toplu işlem (Kurum/Resmi belge hazır ayarı, GPS kaldırma dahil) → Faz 7 Düzenleme → Faz 8 Windows entegrasyonu (dosya ilişkilendirme, sağ tık menüsü, kurulum) → Faz 9 Cila.

## Lisans

MIT — © 2026 Hasan Güler. Üçüncü parti bileşenler için `THIRD_PARTY_NOTICES.md`.
