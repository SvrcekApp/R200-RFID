# R200Example

Komentovaná .NET 10 konzolová aplikace ukazující praktické použití projektu `R200RFID`.

## Spuštění

```powershell
dotnet run --project R200Example -- COM4 115200
```

Pokud port neuvedeš, aplikace se na něj zeptá. Výchozí rychlost je 115200 bit/s.

## Příklady

1. **Nekonečné načítání tagů** - spustí polling s maximálním počtem cyklů, zpracovává notifikační rámce a po dokončení polling znovu spustí. `Ctrl+C` inventory korektně zastaví a vrátí aplikaci do menu.
2. **Zápis do tagu** - nastaví Select podle zadaného EPC a zapíše UTF-8 text do User memory. Liché množství bajtů doplní nulou na celé 16bitové slovo.
3. **Region a frekvence** - nastaví region, vypne hopping, převede frekvenci na channel index a konfiguraci přečte zpět.
4. **Aktuální RF konfigurace** - zobrazí region, kanál, vypočtenou frekvenci a vysílací výkon.

Používej pouze frekvence a výkon povolené místními předpisy a hardwarem připojené antény.
