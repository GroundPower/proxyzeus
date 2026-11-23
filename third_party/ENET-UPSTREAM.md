# ENet kaynağı

- Upstream: https://github.com/lsalzman/enet
- Sabit commit: `5a9c537fd464b3c6d3c55e1d3bd47588faf71b42`
- Lisans: MIT, `enet/LICENSE`.
- Kaynaklar `enet/` içinde mevcut; derleme sırasında internetten kod çekilmez.

Zeus değişiklikleri: ENetHost sonuna iki bağımsız zarf bayrağı; başlangıçta sıfırlama; çıkış başlığının önüne altı bayt eklenmesi ve CRC/MTU/fragment/sıkıştırma uzunluklarının buna göre hesaplanması; test eşinde bu başlığın okunması. Giriş başlığı için tam uzunluk kontrolü de eklendi. Normal istemci tarafında klasik giriş/çıkış kullanılır; uzak bağlantıda opsiyonel Ubisoft çıkış zarfı açılır. Test için eklenen incoming-prefix bayrağı kullanıcı arayüzünden açılmaz.

Protokol zarfı yerel karşı uçla checksum, sıkıştırma ve parçalama dahil test edildi. Bu doğrulama, güncel resmî Growtopia sunucusunun aynı protokolü kullandığına dair bir iddia değildir.

Upstream kaynak dosyalarının diff'i `docs/enet-zeus.patch` içinde saklanır. Zeus kodu Kulo'nun önceden derlenmiş kütüphanelerine bağlanmaz.
