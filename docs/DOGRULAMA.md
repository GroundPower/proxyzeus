# Doğrulama kapsamı

22 Kasım 2026, Windows x64, .NET 10 SDK ve MSVC 19.44 ile doğrulandı.

## Otomatik

`dotnet run --project tests/Zeus.Tests.csproj -c Release`:

1. Gerçek ENet istemci → Zeus → yerel sunucu; iki yön; kanal 1; 20 KB parçalı dönüş verisinin byte eşitliği.
2. Ubisoft çıkış zarfı / klasik giriş; CRC, sıkıştırma ve 60 KB parçalı veri eşitliği.
3. Variant round-trip, hedef port/adres yeniden yazma, token/kapı/UUID korunması; bütün kesik öneklerin reddedilmesi; uzunluk taşması.
4. 20.000 belirlenmiş rastgele/kesik tamponda ayrıştırıcı dayanıklılığı.
5. Birinci sunucudan ikinci sunucuya gerçek ENet geçişi; istemcinin tekrar bağlanması ve giriş baytlarının korunması.
6. Giriş parolası/token/bilinmeyen alanların trafik özetine girmemesi; 1.000 kayıt sınırı.
7. Yerel `/zeus ping` yanıtı ve sunucuya aktarılmaması; normal `/help` komutunun değişmeden aktarılması.
8. Profilin atomik yazılması/okunması; geçersiz port ve meta satır enjeksiyonunun reddi.
9. HTTP bootstrap yanıtı, keşif ayrıştırması, bilinmeyen dosya yolları için 404, yanlış HTTP metodu için 405.
10. Üç kez başlat/durdur ve UDP portunu yeniden açabilme.
11. Windows kullanıcı sertifikasının tekrar kullanımı ve gerçek HTTPS health isteği. Test istemcisi yalnızca bu belirli sertifikayı kabul eder; üretim keşfinde özel TLS kabul kodu yoktur.

`scripts/Test-Hosts.ps1`: geçici hosts dosyasında Install, tekrar Install, Remove, CRLF ve mevcut satırların korunması. Sistem hosts dosyası testte kullanılmaz.

## Görsel / etkileşim

Masaüstü paneli açılarak genel bakış ve ayarlar ekranları incelendi. İlk görsel kontroldeki DPI ve etiket taşmaları düzeltildi; yeni görünüm yeniden kontrol edildi. Panelin yerel bağlantı testi başarı mesajı verdi. Başlat ile istemci bekleme durumuna geçti; Durdur ile kaynakları kapatıp düğme durumlarını güncelledi.

## Hazır Windows paketi

`dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true` ile oluşturulan hazır `ZeusProxy.exe`, kendi `--self-test` modunda çalıştırıldı. İstemci → Zeus → sunucu testi 20 KB parçalı dönüş paketiyle başarılı oldu; işlem çıkış kodu **0**. Bu kontrol yayınlanan uygulamanın native DLL'i bulup gerçek aktarım yapabildiğini doğrular. Yanındaki `zeus_native.dll` ve ENet lisansı pakete dahil edildi.

## Resmî sunucuya hesap bilgisi olmadan bağlantı

22 Kasım 2026 tarihinde `https://www.growtopia1.com/growtopia/server_data.php` adresine standart sertifika doğrulamasıyla yapılan POST isteği HTTP 200 döndürdü; yanıtın `server` ve `port` alanları okunabildi. Bu uç noktadan alınan sunucuya Ubisoft çıkış zarfı açıkken ENet bağlantısı kuruldu ve ağ mesaj türü **1** (server hello) alındı. `scripts/Probe-Endpoint.py` yalnızca el sıkışması gönderdi; oyun giriş paketi, hesap adı, parola veya token göndermedi. Adres ve port geçici olduğundan uygulamaya sabitlenmedi.

Bu kontrol, TLS bootstrap üzerinden resmî Windows istemcisinin yerel proxy'ye yönlendirilmesini veya hesap girişini doğrulamaz.

## Doğrulanmayanlar

Bütün oyun sürümleri, uzun süreli WAN kayıp/yük senaryoları ve IPv6 doğrulanmadı. Kulo çalıştırılmadı. Yukarıdaki ilk el sıkışması kontrolünden sonra aşağıdaki gerçek istemci testi tamamlandı.

## Gerçek Windows istemcisiyle tamamlanan test

22 Kasım 2026, yaklaşık 23:05–23:10 (Türkiye saati): resmî Windows istemcisi **5.62** ile Zeus üzerinden giriş yapıldı. İlk sunucudan oyun alt sunucusuna ve seçilen dünyaya geçişler proxy içinde kaldı. Dünya görüntüsü yüklendi; dünya adı ve karo konumu panelde okundu. Gözlenen oturumda hatalı paket sayacı sıfırdı. Oyun içine `/zeus ping` yazılarak yerel komut yanıtı **181 ms** olarak görüldü. Bu değerin başka zamanlar için sabit olduğu varsayılmaz.

Gerçek test sırasında düzeltilenler: `loginurl` ve ek server_data alanlarının korunması; istemcinin gerçek sürüm/protokol POST isteğinin resmî sunucuya aktarılması; canlı hedef güncellemesi; Windows TLS uyumsuzluğunda yerel OpenSSL ön katmanı; kullanıcı tarafından onaylanan kişisel yerel sertifika; yerel ENet zarfının otomatik algılanması. TLS doğrulaması veya oyun güvenlik kontrolleri kapatılmadı; oyun dosyaları değiştirilmedi.

Ek otomatik testler: modern yerel zarf algılama, klasik CRC'siz istemci ve Ubisoft CRC'siz istemci. Son kaynak kontrolünde **20 test başarılı, 0 başarısız** (0.1.3). Daha önceki 11–19 test sonuçları ara sürümlere aittir.

Çalışan oyun oturumunu kesmemek için son ek uyumluluk derlemesi `artifacts/diagnostic-build` altında hazır tutulur. Resmî bağlantı başlatıcısı, uygulama kapalıyken bu doğrulanmış derlemeyi yayına kopyalar; açık oturum sırasında dosyaların üzerine yazmaz.

Uyumluluk araştırmasında uygulama sahiplerinin [TLS sertifika üretim kaynağı](https://github.com/trixolol/GrowProxy/blob/main/src/tls.js) ve [server_data aktarım kaynağı](https://github.com/vinskeeper/GTProxy/blob/main/src/core/web_server.cpp) incelendi. Zeus'un aktarım ve TLS ön katmanı ayrı yazıldı; bu kaynaklardaki doğrulama kapatma ve ham içerik kaydı yaklaşımları kullanılmadı.

## Dünya geçişinde "Located server, connecting... Timed out" hatası (0.1.2 / 0.1.3)

**Kök neden:** Sunucu `OnSendToServer` gönderdiğinde oyun istemcisi yeni bağlantıyı, eski bağlantısını kapatmadan önce açar. Zeus'un yerel ENet sunucusunda tek bağlantı yuvası olduğu için yeni el sıkışması reddediliyor, istemci 10 saniye sonra zaman aşımına düşüyordu. Gir-çık yapmak eski bağlantı hâlâ yuvayı tuttuğu için sorunu çözmüyordu.

**Düzeltme:** 0.1.2'de yerel tarafa yedek el sıkışması yuvaları eklendi ve yeni CONNECT geldiğinde eski bağlantının kuyruğu temizlenip bırakıldı; eski oturumdan geç gelen olaylar yeni oturumu kapatmıyor. 0.1.3'te yuva sayısı 8'e çıkarıldı ve yeni CONNECT geldiğinde yalnızca son bağlantı değil, kurulmuş/ayrılmakta olan **bütün** eski bağlantılar bırakılıyor; böylece iptal edilen denemeler veya hiç ayrılmayan istemciler yuvaları tüketemez. Yeni test: hiç ayrılmayan 16 eski bağlantıyla ardışık geçiş.

0.1.2 canlı oyunda doğrulandı: sunucular arası geçiş (START2) ve önceki dünyaya dönüş, hatalı paket sayacı sıfır. 0.1.3 `artifacts/diagnostic-build` içinde hazır; açık oturum kesilmesin diye **Zeus Resmi Baglanti** kısayolu bir sonraki açılışta yayına kopyalar.
