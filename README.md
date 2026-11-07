# Isle

**Eine Mac-artige Oberfläche für Windows – komplett im Dark Mode.**
Top-Bar, Dynamic Island, Dock und Startmenü legen sich über dein normales Windows. Deine Programme laufen ganz normal weiter.

## Download

1. Unter **[Releases](../../releases/latest)** die Datei `Isle-Desktop-…-win-x64.zip` herunterladen
2. Entpacken (Rechtsklick → *Alle extrahieren*) und **`Isle.exe`** starten
3. Windows warnt beim ersten Start evtl. („Der Computer wurde durch Windows geschützt“), weil Isle nicht kostenpflichtig signiert ist:
   *Weitere Informationen* → *Trotzdem ausführen*

Läuft auf **Windows 10 (ab Version 2004) und Windows 11**, 64 Bit. Es muss nichts zusätzlich installiert werden.
**Beenden:** `Strg + Alt + Q` oder Isle-Menü (Pillen-Symbol oben links) → *Isle beenden* – die Windows-Taskleiste kommt dann zurück.

## Was drin ist

**Top-Bar (oben)**
- Durchsichtig: Die Schriftfarbe richtet sich nach deinem Hintergrundbild – hell → dunkle Schrift, dunkel → weiße Schrift
  (Isle misst direkt, was hinter der Leiste auf dem Bildschirm ist – links und rechts getrennt, alle 10 Sekunden
  und sofort bei neuem Hintergrundbild). Notfalls fest einstellbar: Isle-Menü → „Leiste: Schrift …“.
  Messwerte stehen im Protokoll `%AppData%\Isle\isle.log`.
- Schriftstärke: Isle-Menü → „Einstellungen …“ → Regler von Dünn bis Fett (wirkt sofort)
- Links: Isle-Menü (Pillen-Symbol; nochmal draufklicken oder daneben klicken schließt es) und Name der aktiven App
- Rechts: grüner/oranger Punkt, wenn Kamera/Mikrofon benutzt werden, Wetter, Akku (nur Laptops), Tastatursprache, Datum und Uhr
- Klick aufs Wetter: Ort festlegen · Klick auf die Uhr: Windows-Benachrichtigungen/Kalender

**Dynamic Island (oben mittig)**
- Läuft Musik: Cover + Balken; Maus drüber → klappt auf
- **Musik:** Cover (Klick holt die App nach vorn), App, Titel, Interpret, Zurück/Pause/Weiter, Fortschritt zum Spulen
- **Geräte:** Ausgabegerät wählen (Lautsprecher, Kopfhörer …), Lautstärke, Stumm
- **Erinnerungen:** z. B. „18:30 Wäsche“ eintippen + Enter → um 18:30 meldet sich die Insel
- **Mond = Fokus:** keine Song-Hinweise mehr (Erinnerungen kommen trotzdem)
- **X** klappt sofort zu · **Mausrad** über der Insel = Lautstärke
- Klickbar ist nur die Insel selbst – direkt darunter kannst du ganz normal klicken

**Dock (unten)**
- Start-Knopf, angeheftete Apps, laufende Apps (mit Punkt)
- Vergrößerung beim Drüberfahren, Name über dem Icon, Hüpfen beim Starten
- Klick auf laufende App: nach vorn holen · nochmal: minimieren · bei mehreren Fenstern: durchwechseln
- **Icons verschieben:** festhalten und zur Seite ziehen, die anderen gleiten weg. Reihenfolge wird gespeichert.
- **Entfernen:** Icon nach oben aus dem Dock ziehen, bis „Entfernen“ erscheint, und loslassen
- **Anheften per Drag & Drop:** Programm, Verknüpfung, Datei oder Ordner aufs Dock ziehen
- **Deine Windows-Taskleiste:** Alle dort angehefteten Apps (auch Store-Apps) kommen automatisch ins Dock.
  Später neu angeheftete holst du über Isle-Menü → „Apps aus Windows-Taskleiste ins Dock holen“.
- **Fenster-Vorschau:** Maus kurz auf einer laufenden App lassen → ein Kärtchen pro Fenster mit Live-Bild.
  Klick holt das Fenster nach vorn, das X oder ein Mittelklick schließt es.
- **Rechtsklick:** Fensterliste, Neues Fenster, Im Dock behalten/entfernen, Alle Fenster schließen, Task beenden

**Startmenü (Windows-Taste)**
- Sofort lostippen zum Suchen, Pfeiltasten + Enter, Esc schließt
- Zuletzt verwendete Dateien, alle Apps (auch Store-Apps), Websuche
- Rechtsklick auf App: Im Dock behalten, Als Administrator, Speicherort öffnen
- Unten rechts: Sperren, Energie sparen, Neu starten, Herunterfahren, Abmelden
- Win+D, Win+E, Win+L usw. funktionieren weiter normal

**Windows-Taskleiste**
- Ist komplett versteckt, solange Isle läuft – auch am unteren Bildschirmrand fährt sie nicht mehr hoch.
- Tray-Icons (Discord, NordVPN, Steam …): **Pfeil ⌃ oben rechts in der Top-Bar** → die Icons erscheinen direkt darunter.
  Linksklick/Doppelklick/Rechtsklick wirken wie in der normalen Taskleiste. Nochmal auf den Pfeil oder daneben klicken schließt.
  Abschaltbar im Isle-Menü („Tray-Icons in der Top-Bar“) – dann blendet der Pfeil wieder die Windows-Taskleiste kurz ein.
- Beim Beenden (Strg+Alt+Q oder Isle-Menü) kommt sie zurück. Stürzt Isle ab oder wird im Task-Manager beendet,
  holt ein kleiner Wächter-Prozess sie automatisch zurück (darum siehst du Isle.exe zweimal im Task-Manager).
- Wieder einschalten: Isle-Menü → „Windows-Taskleiste ausblenden“ abhaken.

## Selbst bauen

1. Einmalig das .NET 8 SDK installieren: `winget install Microsoft.DotNet.SDK.8`
2. Repo herunterladen, **`build.bat`** doppelklicken → baut und startet `dist\Isle.exe`

Läuft schon eine ältere Isle-Version, beendet `build.bat` sie vorher sauber.

## Einstellungen

Alles im Isle-Menü (Pillen-Symbol oben links) und unter *Einstellungen …*: Dock an/aus, Taskleiste ausblenden,
Tray-Icons, Windows-Taste, Autostart, Wetter-Ort, Schriftfarbe und -stärke der Leiste.
Gespeichert wird in `%AppData%\Isle\settings.json`, das Protokoll liegt daneben in `isle.log`.

## Hinweise

- Isle ist ein unabhängiges Hobby-Projekt und gehört weder zu Apple noch zu Microsoft.
- Wetterdaten kommen von [Open-Meteo](https://open-meteo.com) (kostenlos, ohne Konto). Sonst verbindet sich Isle mit nichts.
- Isle greift tief in Windows ein (Taskleiste, Bildschirmränder, Windows-Taste). Beim Beenden – und auch wenn es abstürzt –
  wird alles zurückgestellt.

## Aufbau

| Datei | Inhalt |
|---|---|
| `App.xaml(.cs)` | Start, dunkles Design für Menüs, steuert alle Teile |
| `TopBar.xaml(.cs)` | Top-Bar |
| `Dock.xaml(.cs)` | Dock (Verschieben, Vergrößern, Menü, Drag & Drop) |
| `WindowPreview.cs` | Fenster-Vorschau mit Live-Bildern |
| `WinEvents.cs` | Windows meldet Fensteränderungen (statt Dauerabfrage) |
| `StartMenu.xaml(.cs)` | Startmenü |
| `MainWindow.xaml(.cs)` | Dynamic Island |
| `MediaService.cs` | Windows-Mediensteuerung |
| `AudioDevices.cs` | Ausgabegeräte, Lautstärke |
| `TrayHost.cs` | Empfängt die Tray-Icons aller Programme (und reicht sie an Windows weiter) |
| `TaskbarPins.cs` | Liest die angehefteten Apps der Windows-Taskleiste |
| `Menus.cs` | Menüs/Popups schließen bei Klick daneben |
| `AppCatalog.cs` | Liste aller installierten Apps |
| `WindowTracker.cs` | Welche Apps laufen, Fenster nach vorn holen |
| `ShellIcons.cs` | Scharfe App-Icons |
| `KeyboardHook.cs` | Windows-Taste abfangen |
| `AppBar.cs` | Bildschirmränder reservieren, Taskleiste ausblenden |
| `Weather.cs` | Wetter (Open-Meteo) und Kamera/Mikro-Anzeige |
| `Win32.cs`, `NativeMethods.cs` | Windows-API |
