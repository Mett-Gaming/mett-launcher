# Mett Launcher

Launcher für alle Spiele von [Mett Gaming](https://github.com/Mett-Gaming): installieren, automatisch aktualisieren,
ältere Versionen wählen, Spielzeit sehen und Neuigkeiten lesen. Der Launcher aktualisiert sich selbst.

**Download:** `MettLauncher-win-Setup.exe` aus den [Releases](https://github.com/Mett-Gaming/mett-launcher/releases/latest).
Installiert wird ohne Adminrechte nach `%LocalAppData%\MettLauncher`.

## Woher die Daten kommen

| Was | Quelle |
|---|---|
| Spieleliste, Cover | [`Mett-Gaming/launcher-catalog`](https://github.com/Mett-Gaming/launcher-catalog) → `games.json` |
| Neuigkeiten | [`Mett-Gaming/news`](https://github.com/Mett-Gaming/news) → `news.json` |
| Spielversionen | GitHub-Releases des jeweiligen Spiel-Repos (Zip-Asset, sha256 aus der API) |
| Launcher-Updates | Releases dieses Repos (Velopack) |

Alles ist öffentlich, der Launcher braucht keinen Login. Katalog, News und Release-Listen werden zwischengespeichert,
installierte Spiele lassen sich auch offline starten.

## Aufbau

- `src/MettLauncher.Core` – UI-freie Logik: Katalog, News, Releases (ETag-Cache), Installation (Download → Prüfsumme →
  Staging → atomarer Tausch), Spielstart mit Spielzeit, Bibliotheks-State.
- `src/MettLauncher.App` – Avalonia-12-Oberfläche (MVVM, CommunityToolkit.Mvvm), Self-Update über Velopack.
- `native/` – Mehrspieler: Netzdienst `mett-netd` (Rust, iroh) und Spiele-SDK `mett-net`, siehe [native/README.md](native/README.md).
- `tests/MettLauncher.Core.Tests` – xUnit. Echter KASSE-0-Download nur mit `METT_INTEGRATION_DOWNLOAD=1`.
- Design: Figma-Datei „Mett Launcher“ (Tokens, Komponenten, Screens).

## Entwickeln

```powershell
dotnet test
dotnet run --project src/MettLauncher.App
```

Lokales Paket wie in der CI: `pwsh tools/pack.ps1 -Version 0.1.0` → `dist\`.
Release: Tag `vX.Y.Z` pushen, die CI baut und veröffentlicht die Velopack-Pakete.

Das Icon erzeugt `uv run tools/make-icon.py` aus `assets/icon-source.png`.

## Lizenzen

Schriften: Archivo, Archivo Black, JetBrains Mono (SIL Open Font License, Lizenztexte in `src/MettLauncher.App/Assets/Fonts`).
Icons: [Lucide](https://lucide.dev) (ISC).
