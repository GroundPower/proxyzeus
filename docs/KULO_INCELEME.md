# Kulo kaynak incelemesi ve Zeus kararları

İnceleme tarihi: 22 Kasım 2026. Referans: [Ruzgarefeyavuz/Growtopia-Kulo-Proxy](https://github.com/Ruzgarefeyavuz/Growtopia-Kulo-Proxy), commit `63d8c88c82d2fd8e50417ca740cd73bca317531f`. Bulgular kaynak incelemesidir; Kulo yürütülebiliri ve depodaki önceden derlenmiş kütüphaneler çalıştırılmadı.

## Nasıl çalışıyor?

Windows ağırlıklı bir C++17 uygulaması. Visual Studio çözümü, projeye gömülü değiştirilmiş ENet, HTTP(S) sunucuları, variant/rtparam yardımcıları, items.dat okuyucu, dünya/envanter modeli ve oyun komutları içeriyor.

Akış: istemci `server_data.php` için yerel HTTPS servisine yönlendiriliyor → yerel ENet sunucusu bağlantıyı kabul ediyor → ayrı ENet istemcisi gerçek sunucuya bağlanıyor → iki yöndeki paketler olay işleyicilerinden geçiyor. Sunucu geçişinde `OnSendToServer` içindeki adres ve port tekrar yerel proxy'ye çevriliyor. Oyun komutları gelen metin içinden yakalanarak paket üretiyor veya diyalogları yanıtlıyor.

| Dosya / modül | Sorumluluk |
|---|---|
| `proxy/proxy.cpp` | Başlangıç, HTTPS, hosts, KeyAuth, ayarlar ve kısayollar |
| `proxy/server.cpp` | ENet bağlantıları, iki yönlü aktarım, dünya/envanter güncellemeleri |
| `proxy/events.cpp` | Metin komutları, diyaloglar, görsel durumlar ve otomasyonlar |
| `proxy/packet.h` | Ağ mesajı ve 56 baytlık oyun başlığı düzeni |
| `proxy/proton/variant.hpp` | Variant listelerinin kodlanması/ayrıştırılması |
| `proxy/world.cpp`, `world.h` | Dünya, tile ek verileri ve item tanımları |
| `proxy/items_dat_decode.h` | Yerel items.dat dosyasının sürüme göre okunması |
| `proxy/Pathfinder` | Yol arama ve hareket yardımcıları |
| `proxy/enet` | İstemci/sunucu UDP güvenilir iletim katmanı; Ubisoft çıkış biçimi değişiklikleri |

## Özellik envanteri

Kaynakta komut/diyalog olarak görülen başlıca gruplar: auto-surgery, toplama ve farm yardımcıları; otomatik pull/ban; hızlı drop/trash/vending; kıyafet/isim/ünvan görünümü; konum, hareket ve yol bulma; F1–F12 kısayolları; dünya geçişi, item arama, izleme bilgileri; spam ve casino/reme/qeme yardımcıları.

Bu envanter, özelliklerin güncel sunucuda çalıştığı anlamına gelmez. Bir kısmı istemci tarafı görsel değişiklik, bir kısmı normal işlem otomasyonu, bir kısmı sunucu davranışına ve eski protokole bağlıdır.

## Somut bulgular

Satır numaraları yukarıdaki commit içindir. [Sabit kaynak görünümü](https://github.com/Ruzgarefeyavuz/Growtopia-Kulo-Proxy/tree/63d8c88c82d2fd8e50417ca740cd73bca317531f).

| Öncelik | Kanıt | Sonuç | Zeus'taki yaklaşım |
|---|---|---|---|
| Yüksek | `proxy.cpp:124,172,181`; `http.cpp:22,37` | Hosts dosyası truncation ile açılıyor; mevcut DNS satırları başlangıçta/çıkışta silinebilir. | Uygulama hosts'a yazmaz. Ayrı betik yalnızca işaretli bloğu yönetir, çakışmayı kontrol eder ve yedek alır. |
| Yüksek | `proxy.cpp:151`; `events.cpp:3161` | Uzak HTTPS sertifika doğrulaması kapatılmış. | Sunucu keşfinde standart TLS doğrulaması açık; keyfi fallback yok. |
| Yüksek | `utils.cpp:7–40` | Metin okuma son baytı değiştiriyor; boş veri koruması yok. Oyun başlığı kontrolü mesaj önekini tam hesaba katmıyor; ek uzunluk toplamasında taşma riski var. | `ReadOnlySpan`, en az 60 bayt kontrolü, kalan uzunlukla karşılaştırma. Metin son baytı yerinde değiştirilmez. |
| Yüksek | `proton/variant.hpp:376–438` | Dışarıdan gelen variant sayısı, index ve string uzunlukları toplam tampon boyuyla doğrulanmıyor. | Sınırlı variant sayısı, index/tekrar kontrolü, tür ve kalan bayt denetimi. |
| Yüksek | `server.cpp:772–800` | `enet_host_create` sonucu null kontrolünden önce dereference ediliyor. Port açılamadığında çökme olabilir. | Native kaynak oluşturma her aşamada doğrulanır; hata panelde gösterilir. |
| Yüksek | `server.cpp:587,624–625` | Envanterde `capacity()` kadar kopyalama ve nesne aramasında `end()` kontrolü olmadan erişim var. | Bu ayrıştırıcılar taşınmadı; gözlem desteklenen küçük başlıklarla sınırlı. |
| Orta | `events.cpp:1947–1954,2111–2141`; `proxy.cpp:675,689` | Ayrılmış thread'ler, paylaşılan global ayarlar ve thread yaşamından bağımsız capture'lar; kontrollü durdurma eksikliği. | Her aktarıcının native işlemleri tek seri döngüde; iptal ve deterministik dispose. |
| Orta | `server.h:30–31`; `proxy.cpp:149`; `gt.cpp:8` | Sabit sunucu/IP ve eski istemci sürümü varsayımları. | Hedef kullanıcı ayarı/HTTPS keşfi; eski sürüm numarası dayatılmaz. |
| Orta | `items_dat_decode.h:151,381` | 14 üzeri sürümde erken çıkış varken daha sonra 15 dalı var; erişilemeyen dal ve güncel biçim belirsizliği. | Tam items.dat ayrıştırıcısı bu sürüme alınmadı. |
| Orta | `world.cpp:21` | `itemID == itemDefs.size()` durumu dışlanmıyor; boş item listesinde de indeksleme olabilir. | Eski tile/item kodu çalıştırılmaz; yeni gözlem kodu uzunlukları kontrol eder. |
| Orta | `proxy.cpp:36–43,636–660` | KeyAuth başlangıcı/lisans akışı boş uygulama ayarları ve dış binary bağımlılığıyla bağlı. | Zeus'ta harici lisans servisi yok; yerel çalışma dış doğrulamaya bağlı değil. |
| Orta | `events.cpp:4255` | Tracking paketinin tamamı konsola basılıyor. | İçerik yerine yalnızca tanımlı mesaj türleri, boyut, yön ve kanal kaydı. |

Ek sürdürülebilirlik bulguları: büyük bir events modülünde komut ayrıştırma ve paket gönderme iç içe; sayısal `stoi` dönüşümleri birçok yerde hata yakalamadan çağrılıyor; `build.sh` bağımlılık/Windows kodu yönünden tam bir platformlar arası derleme sözleşmesi sunmuyor; OpenSSL ve önceden derlenmiş lib dosyaları kaynak ve ABI yönetimini zorlaştırıyor. Depoda uygulama davranışını doğrulayan otomatik test paketi bulunmadı.

## Harici adresler hakkında doğrulanan sınır

`events.cpp:3168–3170` içinde üçüncü taraf bir HTTP servisine **GET** fallback'i var. İncelenen çağrıda parola veya giriş paketinin bu adrese gönderildiğine dair kanıt yok. Alan adından hareketle “hesap çalıyor” sonucu çıkarılmadı. İlk değerlendirmede harici servis çağrısını araştırdım; doğrulanan bulgu, şifrelenmemiş üçüncü taraf sunucu bilgisi bağımlılığıdır. Önceden derlenmiş `library_x64.lib` / `libcurl.lib` dosyalarının davranışı kaynak incelemesiyle doğrulanamaz; çalıştırılmadılar.

## Zeus'ta daha iyi olan ve henüz eşdeğer olmayan alanlar

Zeus bu kaynak üzerine ad değiştirme işlemi değildir. Uygulama çekirdeği, masaüstü arayüzü, native köprü, testler ve hosts aracı ayrı yazıldı. Bağımlılık olarak Lee Salzman'ın MIT lisanslı upstream ENet kaynağı kullanıldı; protokol için altı baytlık çıkış zarfı desteği eklendi. Kulo'nun uygulama kaynakları veya binary kütüphaneleri Zeus derlemesine katılmıyor.

Gelişen alanlar: Türkçe masaüstü paneli, tekrar başlatılabilir yaşam döngüsü, sınırlandırılmış kuyruklar, gizli içerik almayan trafik özeti, açık hedef ayarları, güvenli varsayılan TLS, hosts koruması ve gerçek ağ testleri.

Eksik/eşdeğer olmayan alanlar: Kulo'nun otomasyon özellik seti, tam dünya/envanter/items.dat çözümlemesi ve güncel resmî Growtopia giriş uyumluluğu. Dolayısıyla v0.1 için “her özelliğiyle Kulo'dan daha üstün” veya “resmî oyuna hazır” sonucu çıkarılmıyor. Teslim edilen sonuç, test edilmiş aktarım çekirdeği ve kullanılabilir yerel kontrol panelidir.

## Lisans durumu

Referans depoda genel bir açık kaynak lisans dosyası bulunmadı; README satışla ilgili bir kısıtlama belirtiyor. Herkese açık olması, otomatik olarak sınırsız yeniden dağıtım izni sağlamaz. Bu nedenle Kulo uygulama kodu yayımlanabilir Zeus paketine dahil edilmedi. Upstream ENet MIT bildirimi hazır paketle birlikte veriliyor. İnceleme amacıyla indirilen referans `reference/kulo` altında; ana `.gitignore` ve paketleme seçimiyle dağıtım dışında tutuluyor.
