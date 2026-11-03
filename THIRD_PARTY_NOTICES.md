# Üçüncü Parti Bileşenler

| Bileşen | Sürüm | Lisans | Amaç |
|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | 11.3.x | MIT | Platformlar arası arayüz |
| Inter yazı tipi | 4.x | SIL Open Font License 1.1 | Arayüz yazı tipi |
| Magick.NET-Q16-AnyCPU | 14.x | Apache 2.0 | Görüntü çözümleme/kodlama |
| ImageMagick (Magick.NET içinde) | 7.1.x | ImageMagick License (Apache 2.0 türevi) | Codec çekirdeği |
| libheif / libde265 (ImageMagick içinde) | — | LGPL-3.0 | HEIC/HEIF çözümleme |
| libraw (ImageMagick içinde) | — | LGPL-2.1 / CDDL-1.0 | RAW çözümleme |
| libavif, libwebp, libjxl, libpng, libjpeg-turbo, libtiff, OpenJPEG, OpenEXR (ImageMagick içinde) | — | BSD / zlib / IJG / libtiff lisansları | Format desteği |
| MetadataExtractor | 2.x | Apache 2.0 | EXIF / IPTC / XMP okuma |
| OpenCvSharp4, OpenCvSharp4.runtime.win | 4.10.x | Apache 2.0 | Stüdyo: panorama, hizalama, harmanlama |
| OpenCV (OpenCvSharp içinde) | 4.10 | Apache 2.0 | Görüntü işleme çekirdeği |
| Avalonia.Android, Xamarin.AndroidX.ExifInterface | — | MIT | Android uygulaması |
| CommunityToolkit.Mvvm | 8.x | MIT | MVVM altyapısı |
| xUnit (yalnızca testler) | 2.9.x | Apache 2.0 | Birim testleri |

Kesin sürümler `obj/project.assets.json` dosyasında görülebilir. LGPL bileşenler Magick.NET tarafından dinamik bağlı yerel kütüphaneler olarak dağıtılır.

**Not (patent):** HEVC (HEIC) çözümleme bazı ülkelerde patent lisansı gerektirebilir. Ticari dağıtım öncesinde değerlendirilmelidir.
