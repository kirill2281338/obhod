# Оформление репозитория на GitHub

## Название репозитория

`obhod`

## Описание (поле About, до 350 символов)

Запрет, прокси для Telegram и VPN в одной программе для Windows. Автоподбор стратегии zapret, ссылки и подписки как в Happ и Incy (VLESS, VMess, Trojan, Shadowsocks), обновления с GitHub, тёмная тема «жидкое стекло».

## Темы (Topics)

```
zapret windows dpi-bypass youtube discord telegram telegram-proxy mtproto vpn xray vless reality sing-box happ wpf dotnet russia censorship-circumvention
```

## Ссылка (Website)

Ссылка на скачивание последнего установщика: `https://github.com/kirill2281338/obhod/releases/latest/download/obhod-setup.exe`

## Картинка для превью (Social preview)

`docs/logo.png` или `docs/screenshots/main.png` (Settings → General → Social preview, рекомендуемый размер 1280×640).

## Публикация релиза

- Укажите одну версию в `src/app/obhod.csproj` и `src/Setup/Setup.csproj`.
- Соберите установщик через `build.ps1` с чистым комплектом Flowseal.
- Опубликуйте стабильный релиз с тегом `vX.Y.Z` и приложите `publish/setup/obhod-setup.exe` под этим же именем.
- После публикации проверьте ссылку скачивания и запустите предыдущий установщик, чтобы проверить предложение обновления.

## Что не кладём в репозиторий

Готовые `.exe`, `payload.zip`, папку `publish\` и бинарники запрета: они собираются из исходников или скачиваются программой. Это уже прописано в `.gitignore`.
