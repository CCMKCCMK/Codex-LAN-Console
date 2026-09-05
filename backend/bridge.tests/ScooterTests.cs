using System.Text.Json;
using CodexLanBridge.Commute;

internal static class ScooterTests
{
    internal static void Run(Action<bool, string> assert)
    {
        var settings = new ScooterSettings { Charger = new("Campus test", 32.88, -117.23) };
        var flat = ScooterStore.Effort(settings, 1000, 0, 0);
        assert(ScooterStore.Effort(settings, 1000, 100, 0) > flat, "Climbing increases scooter energy estimate.");
        assert(ScooterStore.Effort(settings, 1000, 0, 10000) >= flat * .8, "Downhill cannot imply unlimited regeneration.");
        var d = new ScooterData { Settings = settings };
        assert(ScooterStore.Model(d).Cycles == 0 && ScooterStore.Model(d).ConservativeMeters < 15000, "Cold start is conservative, not a calibrated claim.");
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (int i = 0; i < 5; i++)
        {
            var id = i.ToString(); d.Cycles.Add(new(id, now.AddDays(-i), now, "depleted"));
            d.Rides.Add(new() { Id = id, CycleId = id, StartedAt = now.AddMinutes(-20), StoppedAt = now,
                Meters = 10000 + i * 100, TerrainMeters = 10000 + i * 100, LastPoint = new(1, now, 32.88, -117.23, 5) });
        }
        assert(ScooterStore.Model(d).Cycles == 5 && ScooterStore.Model(d).BacktestErrorPercent.HasValue, "Complete cycles train capacity and expose chronological backtest error.");
        d.Rides[0].Gaps = 1; d.Rides[1].ManualDistance = true; d.Cycles[2] = d.Cycles[2] with { EndReason = "recharged" };
        assert(ScooterStore.Model(d).Cycles == 2, "Gaps, manual mileage and partial recharges must not silently train full capacity.");
        var dir = Path.Combine(Path.GetTempPath(), "codex-scooter-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ScooterStore(dir, settings.Charger!);
            var full = new ScooterAction("full", Guid.NewGuid().ToString());
            store.Apply(full); store.Apply(full);
            assert(store.Get().Cycles.Count == 1, "Retrying a full-charge command is idempotent.");
            var state = store.Apply(new("start", Guid.NewGuid().ToString())); var ride = ScooterStore.Active(state)!;
            var t = ride.StartedAt;
            var p1 = new ScooterPoint(1, t, 32.8800, -117.23, 5, 50);
            var p2 = new ScooterPoint(2, t.AddSeconds(10), 32.8805, -117.23, 5, 55);
            store.Add(new(ride.Id, [p1, p2])); var count = store.Get().Rides[0].Meters;
            store.Add(new(ride.Id, [p1, p2]));
            assert(count > 40 && count < 65 && store.Get().Rides[0].Meters == count, "GPS distance is measured; replay does not double count.");
            store.Add(new(ride.Id, [new(3, t.AddSeconds(11), 33.0, -117.23, 5, 500)]));
            assert(store.Get().Rides[0].Rejected == 1, "GPS teleport is rejected.");
            var reloaded = new ScooterStore(dir, settings.Charger!);
            assert(reloaded.Get().Rides[0].Meters == count, "Scooter distance survives a Bridge restart.");
            var stopped = reloaded.Apply(new("stop", Guid.NewGuid().ToString(), ride.Id));
            reloaded.Apply(new("stop", Guid.NewGuid().ToString(), ride.Id));
            assert(ScooterStore.Active(stopped) is null, "Phone and web stop receipts can safely race.");
            var bad = false;
            try { reloaded.Update(new(stopped.Revision, settings with { AlertSeconds = 1 })); } catch (ArgumentException) { bad = true; }
            assert(bad, "Reminder loops have a minimum interval to avoid distracting notification storms.");
        }
        finally
        {
            var absolute = Path.GetFullPath(dir);
            if (absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
}
