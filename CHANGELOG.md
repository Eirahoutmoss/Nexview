# Değişiklik Günlüğü

## 0.7.0 — 2026-11-04
- NexView Stüdyo (ayrı pencere, araç çubuğundaki katman simgesi): Makro, HDR, Panorama, Astro.
- OpenCV motoru: ECC hizalama + piramit (PMax) ya da derinlik haritası odak birleştirme; ORB hizalama + Mertens HDR (Doğal/Canlı/Dramatik);
  Stitcher panorama (küresel/düz, ufuk düzeltme, pozlama telafisi, çok bantlı harmanlama), ortak alana otomatik kırpma; 16 bit iş akışı.
- Kare şeridi (küçük resim, EXIF, EV rozeti, sıralama, dahil/hariç), EXIF tabanlı ön denetim uyarıları, hızlı önizleme, önce/sonra.
- Canlı önizleme (önbellekli: hizalama ve okuma tekrarlanmaz), her ayar için açıklama.
- Makro: Karma yöntem (piramit ayrıntı + derinlik haritası taban) ve hale bastırma. HDR: hayalet giderme, Doğal/Canlı/Dramatik ton.
- Panorama: küçük gezegen ve tünel çıktısı, GPano 360° bilgisi; 360° görüntüleyici (Stüdyo'dan ve ana görüntüleyiciden).
- Uygulama içi güncelleme (masaüstü ve Android): GitHub'daki son sürümü denetler, indirip kurar.
- Kaldırıldı: yetersiz kalitedeki saf C# birleştirme motoru (Android'deki makro/HDR/panorama kartları dahil).

## 0.6.0 — 2026-10-06
- Android uygulaması (NexView-0.6.0-android.apk, Android 10+): galeri, dokunmatik görüntüleyici, bilgi/GPS, Paylaş ile alma.
- Mobil dönüştürme: hazır ayarlar, format/kalite/boyut/metadata/dosya sınırı; galeriye kaydet, paylaş, PDF oluştur.
- Mobil düzenleyici: ton, kırpma/döndürme/düzeltme, işaretleme ve gizleme, geri al/yinele, önce/sonra.
- Belge tarama: kamerayla çekim, otomatik kenar bulma, perspektif düzeltme, okunaklılık filtreleri.
- Ortak çekirdek: saf C# geometri motoru (perspektif, döndürme, kırpma) ve belge algılayıcı.
- Masaüstü: işaretler Ayarla aracında da görünür.

## 0.5.0 — 2026-10-06
- Yerleşik kurulum sihirbazı (NexView-0.5.0-Setup.exe): yönetici izni gerektirmez, günceller, açık kopyayı kapatır.
- AFOT topluluk sürümü (taşınabilir, Hakkında bölümünde ithaf).
- Astro stack: yıldız izi, ortalama, medyan, sigma kırpma; yıldız eşleştirmeli otomatik hizalama; 16 bit çıktı.
- Komşu görüntüleri arka planda ön yükleme ile anında gezinme.
- Oturumu geri yükleme, klavye kısayolları ekranı (F1), tanılama raporu (zip), simge düğmeleri için ekran okuyucu adları.
- Düzeltme: işaretleme ve belge köşeleri araçlarında fare olaylarının görüntüleyiciye kaçması.

## 0.4.0 — 2026-10-06
- Windows entegrasyonu: kullanıcı düzeyinde dosya ilişkilendirme ("Birlikte aç", Varsayılan Uygulamalar), Explorer sağ tık alt menüsü.
- Komut satırı: --convert (arayüzsüz dönüştürme, hazır ayarlar dahil), --tab, --register/--unregister, --install/--uninstall.
- Uygulama içinden kullanıcı bazlı kurulum/kaldırma, Başlat menüsü ve masaüstü kısayolu; dosya türü seçimi.
- Inno Setup kurulum betiği (yönetici izni gerektirmez, kaldırınca kayıtları temizler), taşınabilir paket ve sürüm betiği.
- Windows'a özgü kod NexView.Platform.Windows projesine taşındı.

## 0.3.0 — 2026-10-06
- Tahribatsız düzenleyici: ton ayarları ve otomatik seviye; kırpma (oran hazırları, biyometrik 50×60), döndürme, çevirme, ufuk düzeltme.
- İşaretleme (ok, kutu, elips, metin, vurgu) ve kişisel veri gizleme (bulanıklaştır, pikselleştir, siyah kutu).
- Belge modu: dört köşe perspektif düzeltme ve tarama filtreleri.
- Önce/bölünmüş karşılaştırma, geri al/yinele, yeni dosyaya kaydet / farklı kaydet; metadata korunur.

## 0.2.0 — 2026-10-06
- Dönüştürme motoru: tekli/toplu dönüştürme, hazır ayarlar (Paylaşmaya hazırla, Kurum/Resmi belge, Web, Sosyal, E-posta, Baskı, Arşiv), hedef dosya boyutu, yeniden boyutlandırma, metadata politikası, güvenli yazma ve doğrulama.
- Görüntüleri tek PDF'te birleştirme (A4 / görüntü boyutu).
- Toplu işlem: duraklat/devam/iptal, kalan süre, rapor; "Dönüştürülenler" bölümü.
- Tek pencere, pano kopyala/yapıştır, Geri Dönüşüm Kutusu'na silme, yeniden adlandırma, yazdırma, sağ tık menüsü.
- Animasyonlu GIF/WebP oynatma; FITS desteği ve otomatik germe.
- Türkçe/İngilizce yerelleştirme altyapısı, Ayarlar paneli, marka setinden üretilen uygulama ikonları.

## 0.1.0 — 2026-10-06
- İlk çalışan çekirdek: Avalonia kabuğu, NexView Dark teması, görüntüleyici (zoom/pan/fit/%100/döndür/çevir/tam ekran).
- Magick.NET codec eklentisi, birincil + alternatif çözücü zinciri, RAW gömülü önizleme.
- İçerik imzasıyla format algılama ve uzantı uyuşmazlığı uyarısı.
- Bilgi paneli: dosya, kamera, konum (GPS gizlilik uyarısı), teknik bilgi, ham metadata.
- Film şeridi (sanallaştırılmış, disk önbellekli), sürükle-bırak, favoriler, son açılanlar.
- "Neden açılmıyor?" hata ekranı ve tanılama raporu.
- Geliştirme döngüsü (tools/devloop.ps1), testler.
