# Сторонние компоненты

«обход» — оболочка над чужими открытыми проектами. Их код не входит в этот репозиторий и распространяется по лицензиям авторов.

| Компонент | Как используется | Где взят | Лицензия |
|---|---|---|---|
| zapret-discord-youtube (Flowseal) | стратегии `general*.bat`, списки доменов; копия кладётся в установщик | https://github.com/Flowseal/zapret-discord-youtube | см. файл LICENSE в репозитории |
| zapret / winws (bol-van) | `winws.exe` внутри zapret-discord-youtube | https://github.com/bol-van/zapret | MIT |
| WinDivert (basil00) | драйвер `WinDivert64.sys` и `WinDivert.dll` внутри zapret | https://github.com/basil00/WinDivert | LGPL-3.0 или GPL-2.0 (на выбор) |
| tg-ws-proxy (Flowseal) | прокси для Telegram, скачивается с GitHub | https://github.com/Flowseal/tg-ws-proxy | см. файл LICENSE в репозитории |
| Xray-core (XTLS) | ядро VPN, скачивается по кнопке | https://github.com/XTLS/Xray-core | MPL-2.0 |
| sing-box (SagerNet) | режим «весь трафик» (TUN), скачивается по кнопке | https://github.com/SagerNet/sing-box | GPL-3.0-or-later |
| .NET 10 Desktop Runtime (Microsoft) | среда выполнения, скачивается с сайта Microsoft | https://dotnet.microsoft.com | MIT |
| Simple Icons | логотипы сервисов (`src/app/BrandIcons.cs`) | https://simpleicons.org | CC0-1.0 |
| Oswald, Manrope | шрифты интерфейса и установщика | https://fonts.google.com | SIL Open Font License 1.1 |

Названия и логотипы сервисов принадлежат их владельцам и используются только для обозначения.

## Перед публикацией релиза

- Установщик содержит копию zapret-discord-youtube вместе с `winws.exe` и WinDivert. Положите рядом с ними тексты их лицензий (`LICENSE` из репозиториев zapret-discord-youtube, zapret и WinDivert), как требуют эти лицензии.
- Xray, sing-box и tg-ws-proxy в установщик не входят: программа скачивает их официальные сборки с GitHub на компьютере пользователя.
- Лицензии в таблице указаны по состоянию на ноябрь 2026. Перед публикацией сверьте их с репозиториями авторов.
