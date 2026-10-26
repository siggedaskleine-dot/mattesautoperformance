# Mattes AutoPerformance

Ett Windows 11-program som visar en snygg överblick över datorns hårdvara, hittar saknade uppdateringar och andra fel, och fixar dem med ett klick.

## Funktioner

- **Översikt**: processor, grafikkort, RAM, moderkort/BIOS, lagring och nätverk, plus live-mätare för processor och minne.
- **Hälsokontroll**: letar efter drivrutinsfel, saknade Windows- och drivrutinsuppdateringar, autostartappar, skadade systemfiler, diskproblem, tillfälliga filer, energischema och väntande omstart.
- **Fixa med ett klick**: varje problem har en knapp, och **Fixa allt** kör alla säkra åtgärder i ett svep:
  - installerar Windows- och drivrutinsuppdateringar
  - söker igenom enheterna igen och hämtar saknade drivrutiner
  - reparerar systemfiler med DISM och SFC
  - rensar tillfälliga filer äldre än ett dygn
- **Autostartappar**: slå av och på appar på samma sätt som Aktivitetshanteraren. Det går alltid att ångra.
- **Energischema**: byt till Hög prestanda med ett klick.
- **Spara rapport**: en textfil med all hårdvara och resultatet av hälsokontrollen.

Innan uppdateringar eller reparationer körs skapar appen en återställningspunkt, så att du kan backa via Systemåterställning om något går fel.

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

C# och WPF på .NET 10. Hårdvaruinformationen läses via WMI (`System.Management`), registret och .NET:s nätverks- och diskklasser. Appen kräver administratör (Windows frågar när den startar) eftersom åtgärderna ändrar systemet. Översikten och skanningen ändrar ingenting; bara knapparna gör det, och de frågar först.
