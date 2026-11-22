# Mett Launcher

Launcher für alle Spiele von [Mett Gaming](https://github.com/Mett-Gaming): installieren, automatisch aktualisieren,
ältere Versionen wählen, Spielzeit sehen und Neuigkeiten lesen. Der Launcher aktualisiert sich selbst.

**Download** aus den [Releases](https://github.com/Mett-Gaming/mett-launcher/releases/latest):

- **Windows:** `MettLauncher-win-Setup.exe` – installiert ohne Adminrechte nach `%LocalAppData%\MettLauncher`
  (Startmenü + Desktop). Spiele und Einstellungen liegen getrennt davon in `%LocalAppData%\MettGaming\Launcher`.
- **Linux / Steam Deck (Vorschau):** `MettLauncher.AppImage` herunterladen, ausführbar machen
  (`chmod +x`, bzw. Rechtsklick → Eigenschaften → „Ausführbar“) und starten. Daten liegen unter `~/.local/share/MettGaming/Launcher`.
  Auf dem Steam Deck im Desktop-Modus; optional in Steam „Ein Nicht-Steam-Spiel hinzufügen“ für den Game Mode.

## Linux und Proton

Die Spiele sind Windows-Builds. Unter Linux startet der Launcher sie automatisch über **Proton**:

- **umu-launcher** führt Proton außerhalb von Steam aus (im Steam-Runtime-Container). Ist `umu-run` installiert, wird
  es genutzt, sonst lädt der Launcher die offizielle zipapp (braucht `python3`, auf SteamOS vorhanden).
- **GE-Proton** lädt der Launcher beim ersten Spielstart (einmalig ca. 600 MB, mit Prüfsumme) und hält es über
  Einstellungen → Proton aktuell. Pro Spiel lässt sich über das Zahnrad eine andere Version wählen, auch eine aus Steam.
- Jedes Spiel hat seinen eigenen **Wine-Prefix** unter `~/.local/share/MettGaming/Launcher/prefixes/<spiel>`
  (Zahnrad → „Wine-Prefix öffnen / zurücksetzen“).
- Startet ein Spiel nicht, hilft Einstellungen → Proton → „Proton-Log schreiben“.
- Mehrspieler funktioniert unter Proton genauso: das Spiel spricht per localhost mit dem nativen `mett-netd`.

Linux ist eine **Vorschau**: Die CI baut und startet den Launcher unter Ubuntu und führt ein Windows-Programm über
GE-Proton aus (inkl. Mehrspieler-Verbindung). Echte Spiele auf echter Hardware/Steam Deck sind noch nicht getestet.

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
