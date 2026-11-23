# Zeus Proxy

Kendi bilgisayarında açıp kapatabileceğin Türkçe Windows masaüstü uygulaması. Kulo deposunun incelenmesinden sonra ayrı bir C#/.NET çekirdeği ve MIT lisanslı upstream ENet üzerine geliştirildi.

**Durum: 22 Kasım 2026 tarihinde bu bilgisayardaki resmî Windows istemcisi 5.62 ile hesap girişi, sunucu geçişi, dünya yükleme ve oyun içinde `/zeus ping` yanıtı doğrulandı. Kulo'nun bütün oyun otomasyonları bu sürümde bulunmuyor.**

## Bu bilgisayarda resmî oyunu açma

Proje klasöründeki **Zeus Resmi Baglanti** kısayolunu kullan. Kısayol güncel sunucu bilgisini alır, kayıtlı yerel sertifikayı kullanır, Zeus ile yerel OpenSSL HTTPS katmanını açar ve hosts bloğu için Windows yönetici onayını ister. Mevcut Zeus oturumu açıksa ikinci bir oturum başlatmaz.

Bu bilgisayarda Node.js kullanılır; yerel TLS anahtarı ve sertifikası `%LOCALAPPDATA%\ZeusProxy\tls` altındadır. Özel anahtar dağıtım paketine dahil edilmez. Sertifika güveni Windows'ta kullanıcı tarafından onaylandı. Oyun giriş alan adı `login.growtopiagame.com` yönlendirilmez; resmî giriş sistemi korunur.

Oyun içinde `/proxy`, `/ping`, `/status` ve `/pos` komutlarını deneyebilirsin. Durdur/kapat sonrasında yönetici oturum yardımcısı yalnızca kendi hosts bloğunu otomatik kaldırır. Oyunu doğrudan açmak için bu temizliğin tamamlanmasını bekle. TLS yardımcı işlemi de ana Zeus işlemi/yerel servis kapanınca kendisini kapatır.

İstemcinin gerçek sürüm/protokol POST verisi, sertifikası doğrulanan resmî HTTPS uç noktasına iletilir. Hosts dönüşünü önlemek için keşif bağlantısında resmî DNS'ten alınmış IP kullanılır; TLS sunucu adı ve sertifika doğrulaması korunur. Yanıttaki giriş adresi ve ek alanlar saklanır, oyun hedefi her keşifte güncellenir.

## Çalıştırma

Hazır pakette `ZeusProxy.exe` dosyasını aç. Yanındaki `zeus_native.dll` dosyasını aynı klasörde tut. Hazır Windows x64 paketi .NET kurulumunu gerektirmez.

1. **Genel bakış → Yerel bağlantı testi** ile gerçek istemci → Zeus → test sunucusu aktarımını doğrula. İnternet ve oyun hesabı gerekmez.
2. **Bağlantı ayarları** bölümünde hedef sunucu adresini ve UDP portunu gir. İlk açılıştaki `127.0.0.1:17091` bir örnek hedeftir; resmî sunucu adresi değildir.
3. **Başlat** düğmesine bas. Zeus, yalnızca `127.0.0.1:17191` üzerinde bir yerel istemci bekler. Yerel UDP portunu değiştirebilirsin.
4. İstemcini bu yerel adrese yönlendir. Doğrudan adres destekleyen özel istemciler bu adrese bağlanabilir. `server_data.php` kullanan istemciler için aşağıdaki kurulum gerekir.
5. **Durdur** düğmesi Zeus bağlantılarını ve etkinleştirildiyse yerel HTTP(S) servisini kapatır.

## server_data ve resmî istemci

Bu bilgisayarda yukarıdaki resmî bağlantı akışı doğrulandı. Farklı cihazlar, istemci sürümleri ve giriş yöntemleri ayrıca doğrulanmalıdır. Resmî hedef için Ubisoft çıkış zarfı kullanılır; yerel istemcide klasik/Ubisoft çerçevesi otomatik algılanır. CRC kullanmayan yerel istemciler için ek uyumluluk da test paketinde doğrulanmıştır.

- **Sunucu bilgisini al**, belirtilen HTTPS uç noktasına POST yapar; dönen `server`, `port`, `meta` değerlerini okur. TLS sertifikası varsayılan Windows/.NET doğrulamasından geçer. Sürüm/protokol isteniyorsa POST verisini güncel istemci gereksinimlerine göre gir. Kulo'daki eski sabit IP, 3.99 sürümü ve 161 protokolü kopyalanmadı.
- Keşfi **hosts yönlendirmesinden önce** çalıştır. Aksi halde resmî alan adı yerel servise dönebilir. Kullanıcı bilgisayarı için otomatik DNS veya TLS denetimi kapatma yapılmaz.
- `Yerel server_data servisini aç` seçeneği, `http://127.0.0.1:8080/growtopia/server_data.php` üzerinden yerel proxy adresini döndürür. HTTP portu ayrı, oyun UDP portu ayrıdır.
- `HTTPS kullan` seçeneğinde kullanıcıya özel bir sertifika oluşturulur; özel anahtar Windows **CurrentUser / My** deposunda tutulur. Genel sertifika `.cer` olarak dışa aktarılabilir. Güvenilir kök deposuna otomatik ekleme yapılmaz.
- Standart HTTPS isteyen istemciler için yerel servis portunun genellikle **443** olması gerekir. İstemci sertifikaya güvenmiyorsa bu sürüm TLS denetimini atlatmaz. Sertifika sabitlemesi veya farklı giriş akışı varsa ek uyarlama gerekir.

Gerekirse `Hosts.ps1` betiğini **yönetici PowerShell'de**, betiğin bulunduğu dizinde kullan:

```powershell
.\Hosts.ps1 -Action Install
.\Hosts.ps1 -Action Remove
```

Betik `# BEGIN ZEUS PROXY` / `# END ZEUS PROXY` arasına kendi satırlarını ekler; mevcut dosyayı tamamen yeniden yazan Kulo yaklaşımını kullanmaz. Atomik değiştirmeden önce zaman damgalı yedek alır. Önceden başka Growtopia yönlendirmesi varsa işlem durur. Uygulama bu betiği otomatik çalıştırmaz. Oyunu doğrudan açarken eklediğin Zeus bloğunu kaldır.

## Bu sürümde olanlar

- Türkçe koyu tema, başlat/durdur, gerçek zamanlı sayaçlar ve trafik grafiği.
- Klasik ENet; isteğe bağlı altı baytlık Ubisoft çıkış zarfı, CRC ve range coder.
- Güvenilirlik bayrakları ve kanal numarası korunan iki yönlü aktarım.
- Sunucu bağlantısı kurulurken sınırlı kuyruk; bağlantı zaman aşımı ve temiz kapatma.
- `OnSendToServer` geçişinde yerel adresi yeniden yazma; diğer variant alanlarını koruma.
- Paket başlığı, ek veri ve variant uzunluklarında sınır kontrolleri.
- Son 1.000 paketin içeriksiz özeti, filtre, görünümü duraklatma ve CSV dışa aktarma.
- Giriş verileri, sohbet ve tokenlar trafik kaydına alınmaz. Bilinmeyen metin alanları da gizlenir.
- Profil ve favori dünya kaydı; yerel HTTP(S) bootstrap ve HTTPS sunucu keşfi.
- Dünya adı, karo konumu ve gözlenen oyuncu sayısı; eski/uyumsuz dünya şemasında oturum alanları boş kalabilir.
- Oyun içi komutlar ve internet gerektirmeyen bağlantı testi.

## Komutlar

| Komut | İşlev |
|---|---|
| `/proxy` | Proxy hakkında bilgi ve tüm komutların açıklaması |
| `/options` | Reme, QQ, Ping, Anti Gravity ve Path için aç/kapat kutuları, her birinin altında kısa açıklama, kalıcı kaydet düğmesi |
| `/status` | Bağlantı ve dünya bilgisi |
| `/ping` | Aç/kapat: isminin solunda `[ms]` ping etiketi. Renk: 100 ms altı yeşil, 200 altı sarı, 400 altı kırmızı, üstü siyah. Saniyede bir güncellenir |
| `/pos` | Gözlenen hareket paketindeki karo konumu |
| `/fav` | Kaydedilmiş favori dünyalar |
| `/warp DUNYA` | Normal dünya geçiş isteği gönderir |
| `/back` | Gözlenen önceki dünyaya geçiş ister |
| `/path` | Aç/kapat. Açıkken **Shift + Sol Tık** ile seçilen karoya erişimin olan en kısa rotadan gidilir; bir bloğa tıklanırsa yanındaki boş karoya gidilir. Path açıkken oyunun yakınlaştırmasını Zeus yönetir: fare tekerleği oyun yerine Zeus'a gider ve Zeus her adımda oyuna `OnZoomCamera` ile kendi sabit yakınlaştırma seviyelerinden birini gönderir. Böylece hedef karo ekran görüntüsü olmadan, yalnızca hesapla bulunur. Bir seviyenin ölçeği, karakterin yakınına yapılan bir Shift + Sol Tık ile bir kez öğrenilir ve `camera.json` içinde kalıcı olarak saklanır. |
| `/path map` | Bulunduğun dünyanın karo haritasını açar. Açık bir karoyu tıklayarak ilk rotada da uzaktaki bir hedefi seçebilirsin. |
| `/path X Y` | Belirtilen karoya en kısa rotayı hesaplar. Karo engelliyse veya erişimin yoksa rota iptal edilir. |
| `/relog` | Bulunduğun dünyadan çıkıp tekrar girer |
| `/antigravity` | Aç/kapat (`/antigravity on` / `off` ile kesin). İstemcide görünmez bir Anti Gravity Generator açarak yerçekimini azaltır; kapatınca relog atmadan yerinde kaldırılır |
| `/reme` | Aç/kapat: rulet sonucunun rakam toplamının son hanesi (23 → 5, 19 → 0) |
| `/qq` (`/qeme`) | Aç/kapat: rulet sonucunun son rakamı (23 → 3) |
| `/keep` | Reme, QQ ve Ping Göstergesi ayarlarını `%LOCALAPPDATA%\ZeusProxy\settings.json` dosyasına kaydeder; Zeus kapatılıp açılsa da bu ayarlarla başlar. Paneldeki **Kaydet ve Kalıcı Yap** düğmesi de aynı işi yapar |

**Anında rulet:** Sunucu rulet sonucunu dönüş başlarken ~1,8 sn gecikmeyle gönderir. Zeus bu gecikmeyi kaldırır, ayrı bir mesaj göndermez; sunucunun kendi satırını ve konuşma balonunu sabit renklerle yeniden yazar: `[REAL] [Oyuncu spun the wheel and got 23!] REME 5 QQ 3` (beyaz metin, renkli sayı, yeşil REME/QQ, turuncu değer). Bu, kendi vuruşun ve diğer oyuncular için aynıdır; modlar kapalıyken REME/QQ kısmı eklenmez. Gecikmesiz gelen, yani oyuncuların elle yazdığı sahte "spun the wheel" balonları `[FAKE]` ile işaretlenir; kendi karakterinin balonu her zaman gerçek kabul edilir.

**[Zeus Proxy] etiketi:** Gelen bütün sohbet/konsol satırlarının başına yalnızca senin ekranında `[Zeus Proxy]` eklenir. Diğer oyunculara hiçbir şey gönderilmez.

**Sınırsız uzaklaştırma (her zaman açık):** Oyun istemcisi zoom sınırını kendi karakterinin `OnSpawn` paketindeki `mstate` değerinden alır. Zeus yalnızca istemciye giden kopyada bu değeri `1` yapar; sunucuya hiçbir şey gönderilmez. Kulo da aynı yöntemi kullanır. Yalnızca görüntüyü etkiler, vuruş menzilini değiştirmez.

**Giriş mesajı:** Sunucu girişi onayladığında sohbete `Proxy baglantisi basariyla kuruldu! Zeus Proxy v0.1.8 aktif ve calisiyor.` ve komut hatırlatması yazılır, ekranın ortasında kısa bir bildirim çıkar. Her girişte bir kez gösterilir; sunucu veya dünya geçişlerinde tekrarlanmaz.

**Tutorial engelleme:** Adında `tutorial` geçen sunucu işlevleri ve tutorial pencereleri istemciye iletilmez. Gelen işlev adları (yalnızca adlar) tanı kaydına yazılır; tutorial farklı bir adla geliyorsa kayıttan bulunup filtreye eklenebilir.

**Komut temizleme:** Oyun istemcisi bazen komutun sonuna geçersiz bir bayt ekler; Zeus komutu ASCII'ye indirgeyerek tanır. Önceki "Unknown command" hatasının nedeni buydu.

**Oyun içi metin:** Oyun yazı tipi Türkçe harfleri içermediği için (ör. `ı` pasta dilimi olarak görünüyordu) oyuna giden bütün Zeus metinleri ASCII'ye çevrilir (`kapalı` → `kapali`).

**Tanı kaydı:** `%LOCALAPPDATA%\ZeusProxy\zeus.log` bağlantı olaylarını, rulet algılamalarını (netID, gecikme, sayı) ve oyuna aktarılan komutların yalnızca ilk kelimesini tutar; paket içeriği, sohbet ve giriş bilgisi yazılmaz. 1 MiB'ta döner.

Komutlar ek önek olmadan yazılır; eski `/zeus warp` gibi yazım da çalışır. Yalnızca tablodaki komutlar yerel olarak işlenir; `/help`, `/msg` gibi diğer bütün oyun komutları değiştirilmeden oyuna aktarılır. `/warp` oyunun supporter komutuyla aynı adı taşır ve proxy tarafından karşılanır. `warp`, kilit veya erişim kontrolünü değiştirmez; sunucunun normal geçiş yanıtını bekler.

## Veri ve sınırlar

Profil `%LOCALAPPDATA%\ZeusProxy\profile.json` içinde tutulur. Ham paket yakalama veya kalıcı trafik günlüğü yoktur. CSV yalnızca kullanıcı dışa aktardığında oluşur. Paket içerikleri iletim sırasında bellekte bulunur. Profilde adresler, favoriler ve bootstrap meta değeri yer alır; profil dosyası şifreli değildir.

Bir yerel istemci, iki ENet kanalı, en fazla 4 MiB paket, 8 MiB native bekleme alanı ve bağlantı öncesi en fazla 256 paket / 4 MiB kuyruk desteklenir. Gözlenen oyuncu listesi 1.024 kayıtla sınırlıdır. IPv4 kullanılır. Görünüm, log ve komut kuyrukları da sınırlıdır.

Kulo'daki auto-surgery, otomatik toplama/farm, spam, toplu ban/pull, noclip, hız/yerçekimi değiştirme, görsel kıyafet ve casino otomasyonları bu sürüme taşınmadı. Tam items.dat veya dünya tile veritabanı ayrıştırıcısı bulunmuyor. Bu sürümün geliştirdiği alanlar bağlantı yaşam döngüsü, kayıt gizliliği, test edilebilirlik ve masaüstü kullanım kolaylığıdır.

## Kaynaktan derleme

Windows x64, .NET 10 SDK, Visual Studio 2022 C++ Build Tools ve Windows SDK gerekir. ENet kaynakları `third_party/enet` içinde mevcut; Kulo referans kopyası derlemeye katılmaz.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build.ps1 -Publish
```

İlk komut native DLL, masaüstü uygulaması ve testleri derler. İkinci komut ayrıca `dist\ZeusProxy` içinde bağımsız Windows paketini üretir. Derleme sırasında geliştirme çıktısından çalışan Zeus penceresini kapat; Windows kullanımda olan DLL'lerin üzerine yazılmasını engeller.

Kaynak haritası: `src/Zeus.Core` aktarım/protokol, `src/Zeus.Desktop` WinForms panel, `native` C köprüsü, `tests` gerçek ENet ve ayrıştırıcı testleri, `scripts` derleme/hosts araçları.

Hazır paketin komut satırından yerel tanı modu: `ZeusProxy.exe --self-test "C:\tam\yol\sonuc.txt"`. Sonuç dosyasının üst klasörü mevcut olmalı. Başarıda çıkış kodu 0, hata durumunda 1 döner. Normal kullanımda bu parametreye gerek yoktur.

Kulo incelemesi `docs/KULO_INCELEME.md`, bağımlılık kaynağı ve değişiklikleri `third_party/ENET-UPSTREAM.md`, doğrulama kapsamı `docs/DOGRULAMA.md` içinde.
