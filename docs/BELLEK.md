# Bellek okuyucu

`/path` iki kaynaktan beslenir: oyun penceresinin görüntüsü (karo boyutu ve hizalama) ve oyunun
belleğindeki yakınlaştırma ile karakter konumu. Görüntü tarafı ayar istemez. Bellek tarafı
`memory.json` ile bir kez yapılandırılır; boş bırakılırsa okuyucu sessizce devre dışı kalır ve
`/path` yalnızca öğrenilmiş kamera modeliyle çalışır.

Dosya: `%LOCALAPPDATA%\ZeusProxy\memory.json`. Zeus kapalıyken yaz.

## Alanlar

| Alan | Anlamı |
| --- | --- |
| `Module` | Süreç ve modül adı. Varsayılan `Growtopia.exe`. |
| `AppRva` | `GetApp` erişimcisinin modül içindeki göreli adresi. Build ile değişir. |
| `AppPattern` | `AppRva` kaydığında kullanılacak bayt deseni. Doluysa önce desen taranır; yalnızca boşsa veya desen bulunamazsa `AppRva` kullanılır. |
| `ClientOffset` | `app` işaretçisinden istemciye ofset. |
| `AvatarOffset` | İstemciden yerel oyuncuya ofset. |
| `ZoomOffset` | İstemcideki yakınlaştırma değerinin ofseti (float). |
| `PositionOffset` | Oyuncudaki X konumunun ofseti (float); Y hemen ardından gelir. |

Ofsetler `-1` ise "ayarlanmadı" sayılır. `0` geçerli bir ofsettir.

## Örnek

```json
{
  "Module": "Growtopia.exe",
  "AppRva": 9936000,
  "AppPattern": "48 8B 05 ?? ?? ?? ?? C3",
  "ClientOffset": 0,
  "AvatarOffset": 0,
  "ZoomOffset": 0,
  "PositionOffset": 0
}