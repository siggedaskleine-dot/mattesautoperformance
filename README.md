# Mattes AutoPerformance

Ett Windows 11-program som visar en snygg överblick över datorns hårdvara och (i senare steg) hittar och fixar problem med ett klick.

## Plan

1. **Hårdvaruöversikt** ✅ – processor, grafikkort, RAM, moderkort/BIOS, lagring och nätverk.
2. **Hälsokontroll** ✅ – letar efter drivrutinsfel, saknade uppdateringar, autostartappar och trasiga systemfiler.
3. **Fixa med ett klick** – installerar uppdateringar, stänger av autostartappar, kör SFC/DISM. Skapar en återställningspunkt först.
4. **Färdig exe-fil** – byggs automatiskt av GitHub.

## Ladda ner och köra

1. Gå till fliken **Actions** på GitHub och öppna den senaste lyckade körningen av **Bygg**.
2. Ladda ner **MattesAutoPerformance** under *Artifacts* och packa upp zip-filen.
3. Dubbelklicka på `MattesAutoPerformance.exe`. Inget behöver installeras.

Windows SmartScreen kan varna eftersom filen inte är signerad. Välj *Mer information* → *Kör ändå*.

## Bygga själv

Kräver [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet run --project src/MattesAutoPerformance
```

## Teknik

C# och WPF på .NET 10. Hårdvaruinformationen läses via WMI (`System.Management`), registret och .NET:s nätverks- och diskklasser. Översikten och hälsokontrollen läser bara information och ändrar ingenting på datorn. Kontrollen av systemfiler kräver att appen körs som administratör (knappen "Starta som administratör").
