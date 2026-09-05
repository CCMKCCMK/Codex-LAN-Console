using System.Text.Json;
using CodexLanBridge.Commute;

internal static class CommuteTests
{
    internal static void Run(Action<bool, string> assert)
    {
        var s = new CommuteSettings();
        CommuteStore.Validate(s);
        assert(!s.RemindersEnabled, "Unknown personal schedule must not enable reminders.");
        assert(CommuteStore.Available(s, "bus", "toCampus"), "Bus does not require owning a vehicle.");
        assert(!CommuteStore.Available(s, "bike", "toCampus"), "Must not invent bicycle ownership.");
        s.Vehicles["bike"] = "home";
        assert(CommuteStore.Available(s, "bike", "toCampus") && !CommuteStore.Available(s, "bike", "toHome"), "Vehicle location applies to the correct direction.");
        var summer = CommutePlanner.ParseLocal("2026-09-08T09:00");
        var winter = CommutePlanner.ParseLocal("2026-12-08T09:00");
        assert(summer.Offset.TotalHours == -7 && winter.Offset.TotalHours == -8, "Commute schedules must respect Pacific DST.");
        var rejected = false;
        try { CommuteStore.Validate(s with { Preferred = ["walk", "bus", "bike"] }); }
        catch (ArgumentException) { rejected = true; }
        assert(rejected, "At most two preferences may be persisted.");
        var now = summer.AddHours(-2);
        var itinerary = JsonSerializer.SerializeToElement(new { legs = new[] { new {
            mode = "WALK", distance = 1500, duration = 900, startTime = summer.AddMinutes(-15).ToUnixTimeMilliseconds(),
            endTime = summer.ToUnixTimeMilliseconds(), from = new { name = "Origin" }, to = new { name = "Destination" }
        } } });
        var state = new CommuteState(0, s, null, []);
        var walk = CommutePlanner.Normalize(itinerary, "walk", new("toCampus", null, true), state, summer, now)!;
        assert(walk.Minutes == 21, "Walk must use configured speed plus preparation, not OTP's faster default.");
        assert(walk.ArriveAt == summer.AddMinutes(-5), "Arrival target retains safety buffer.");
        assert(walk.LeaveAt == summer.AddMinutes(-26), "Departure includes full door-to-door duration.");
        assert(CommutePlanner.Normalize(itinerary, "bus", new(), state, summer, now) is null, "Walk-only OTP alternatives must not masquerade as transit.");
        var file = Path.Combine(Path.GetTempPath(), "codex-commute-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new CommuteStore(file);
            store.Update(new(0, s));
            var fresh = new CommuteStore(file);
            assert(fresh.Get().Settings.Vehicles["bike"] == "home", "Vehicle location survives restart.");
            var active = fresh.Start(new("toCampus", "bike", 1500, 10));
            assert(active.ActiveTrip is not null && active.Location == "travelling", "Starting a journey records its state.");
            var finish = fresh.Finish(new(active.ActiveTrip!.Id));
            assert(finish.Settings.Vehicles["bike"] == "campus" && finish.History.Count == 1, "Finishing a bike trip moves only the bike and saves history.");
            assert(finish.Settings.Vehicles["car"] == "unavailable", "Travel does not move unrelated vehicles.");
            var stale = false;
            try { fresh.Update(new(0, s)); } catch (InvalidOperationException) { stale = true; }
            assert(stale, "Stale devices cannot overwrite newer settings.");
            var ret = fresh.Start(new("toHome", "bus", 1500, 20));
            var cancel = fresh.Finish(new(ret.ActiveTrip!.Id, true));
            assert(cancel.History.Count == 1 && cancel.Location == "campus", "Cancelled journeys do not train estimates or move the user.");
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
