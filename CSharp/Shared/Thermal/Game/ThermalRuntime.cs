using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Weather;
using ScpItdMod.Stamina;

namespace ScpItdMod.Thermal {
    internal static class ThermalRuntime {
        private sealed class Garment {
            internal readonly float[] Wet = new float[ThermalBody.Regions];
            internal readonly ThermalGarment Profile;
            internal uint Updated;
            internal Garment(ThermalGarment profile) => Profile = profile;
        }
        private sealed class Subject {
            internal readonly ThermalBody Body = new ThermalBody();
            internal readonly float[] SkinWet = new float[ThermalBody.Regions];
            internal readonly Affliction[] Frost = new Affliction[6];
            internal readonly float[] Applied = new float[6];
            internal readonly Item[] Worn = new Item[Slots.Length];
            internal Affliction Cold, Chill, Heat, Pain, Shock, WetCold;
            internal readonly Affliction[] Dexterity = new Affliction[2], Gait = new Affliction[2];
            internal ThermalReading Reading;
            internal double ReadAt = double.NegativeInfinity;
            internal readonly float[] Warmth = new float[LimbTypes.Length];
            internal float LegacyWarmth;
            internal int Faults;
            internal bool Quarantined;
        }
        private static readonly InvSlotType[] HeatedSlots = { InvSlotType.OuterClothes, InvSlotType.Bag, InvSlotType.RightHand, InvSlotType.LeftHand };
        private static readonly Identifier BatteryTag = "mobilebattery".ToIdentifier(), LegacyWarmId = "pp_warmth".ToIdentifier();
        private static readonly InvSlotType[] Slots = { InvSlotType.InnerClothes, InvSlotType.OuterClothes, InvSlotType.Head, InvSlotType.Bag, InvSlotType.HealthInterface, InvSlotType.Headset };
        private static readonly LimbType[] LimbTypes = { LimbType.Head, LimbType.Torso, LimbType.LeftHand, LimbType.RightHand, LimbType.LeftFoot, LimbType.RightFoot };
        private static readonly ThermalRegion[] WarmthRegions = { ThermalRegion.Head, ThermalRegion.Trunk, ThermalRegion.Hands, ThermalRegion.Hands, ThermalRegion.Feet, ThermalRegion.Feet };
        private static readonly Identifier ColdId = "scpitd_cold".ToIdentifier(), FrostId = "scpitd_frostbite".ToIdentifier(), ChillId = "scpitd_chilledskin".ToIdentifier(), HeatId = "scpitd_overheated".ToIdentifier(), WarmId = "scpitd_warmth".ToIdentifier();
        private static readonly Identifier DexterityId = "scpitd_coldhands".ToIdentifier(), GaitId = "scpitd_coldfeet".ToIdentifier(), PainId = "scpitd_rewarmingpain".ToIdentifier(), ShockId = "scpitd_coldshock".ToIdentifier(), WetColdId = "scpitd_wetcold".ToIdentifier();
        private static readonly Dictionary<Character, Subject> subjects = new Dictionary<Character, Subject>();
        private static readonly Dictionary<Item, Garment> garments = new Dictionary<Item, Garment>();
        private static readonly Dictionary<Identifier, (ThermalGarment Garment, InvSlotType Slot)> profiles = new Dictionary<Identifier, (ThermalGarment, InvSlotType)>();
        private static readonly List<Character> removedPeople = new List<Character>();
        private static readonly List<Item> removedItems = new List<Item>();
        internal static ThermalCatalog Catalog { get; private set; }
        internal static bool Ready { get; private set; }
        internal static bool Enabled { get; private set; } = true;
        internal static void SetEnabled(bool enabled) {
            if (!WeatherService.Authority) throw new InvalidOperationException("Cold is controlled by the server");
            Enabled = enabled;
            if (enabled) return;
            IReadOnlyList<Character> people = Humans.All;
            ThermalMap.BeginReceivers();
            Span<Vector2> receiverPoints = stackalloc Vector2[1 + ThermalBody.Regions * 2];
            for (int i = 0; i < people.Count; i++) {
                Character c = people[i];
                if (c.Removed || c.IsDead || c.AnimController == null || c.CharacterHealth == null || c.Params.IsMachine || c.GodMode || c.DisabledByEvent || subjects.TryGetValue(c, out Subject known) && known.Quarantined) continue;
                int count = RadiationPoints(c, receiverPoints);
                ThermalMap.RegisterReceiver(receiverPoints.Slice(0, count));
            }
            for (int i = 0; i < people.Count; i++) {
                Character c = people[i];
                if (c.Removed || c.CharacterHealth == null) continue;
                ClearLegacy(c);
                foreach (var pair in c.CharacterHealth.afflictions) {
                    Identifier id = pair.Key.Identifier;
                    if (id == ColdId || id == FrostId || id == ChillId || id == HeatId || id == DexterityId || id == GaitId || id == PainId || id == ShockId || id == WetColdId) pair.Key.Strength = 0;
                }
            }
            subjects.Clear(); garments.Clear(); accumulator = 0;
        }
        private static float accumulator;
        private static uint sequence;
        private static uint step;
        private static ThermalTraceCapture traceCapture;
        private static string lastCapturePath, lastCaptureFailure;
        internal static void Load() {
            Catalog = ThermalCatalog.Load(ModAssets.Resolve("Content/Thermal/thermal.xml")); profiles.Clear();
            foreach (ThermalGarment garment in Catalog.Garments.Values) {
                if (!Enum.TryParse(garment.Slot, out InvSlotType slot) || Array.IndexOf(Slots, slot) < 0) throw new InvalidOperationException("Unsupported thermal slot " + garment.Slot);
                profiles.Add(garment.Identifier.ToIdentifier(), (garment, slot));
            }
            foreach (Identifier id in new[] { ColdId, FrostId, ChillId, HeatId, WarmId, DexterityId, GaitId, PainId, ShockId, WetColdId }) if (!AfflictionPrefab.Prefabs.TryGet(id, out _)) throw new InvalidOperationException("Missing thermal affliction " + id);
        }
        internal static void Begin() => Ready = true;
        internal static string Diagnostics() {
            string map = Submarine.MainSub?.Info?.Name ?? "(no main submarine)";
            bool matches = WeatherService.Config?.Applies(map) ?? false;
            var text = new StringBuilder(768);
            text.AppendFormat(CultureInfo.InvariantCulture,
                "Thermal runtime: map='{0}', weatherActive={1}, configuredMapMatch={2}, synchronized={3}, ready={4}, enabled={5}, ambient={6:0.0}C.\n",
                map, WeatherService.Active, matches, WeatherService.Synchronized, Ready, Enabled, WeatherService.Current.Temperature);
            if (!matches) text.Append("  Weather configuration does not include this submarine name.\n");
            else if (!WeatherService.Active) text.Append("  Weather round did not activate; thermal simulation cannot start.\n");
            else if (!Ready) text.Append("  Thermal setup did not finish; check the preceding LuaCs error.\n");
            else if (!WeatherService.Synchronized) text.Append("  Waiting for the authoritative weather snapshot.\n");
            else if (!Enabled) text.Append("  Cold simulation was disabled by the server.\n");
            if (traceCapture != null) text.Append("Thermal capture: active=").Append(traceCapture.Active).Append(", path=").Append(traceCapture.Path).Append(", error=").Append(traceCapture.Failure ?? "none").Append(".\n");
            else if (!string.IsNullOrEmpty(lastCapturePath)) text.Append("Thermal capture: stopped, path=").Append(lastCapturePath).Append(", error=").Append(lastCaptureFailure ?? "none").Append(".\n");
            text.Append(ThermalMap.Diagnostics());
            return text.ToString();
        }
        internal static void Reset() { StopCapture(); Ready = false; subjects.Clear(); garments.Clear(); removedPeople.Clear(); removedItems.Clear(); accumulator = 0; sequence = step = 0; ThermalMap.Reset(); ThermalNetwork.Reset(); }
        internal static string CaptureStart() {
            if (!WeatherService.Authority) throw new InvalidOperationException("Thermal capture must be started on the server");
            if (!Ready || !WeatherService.Active || !WeatherService.Synchronized || !Enabled) throw new InvalidOperationException("Thermal capture requires an active, synchronized thermal simulation");
            StopCapture("replaced");
            string map = Submarine.MainSub?.Info?.Name ?? "unknown";
            traceCapture = ThermalTraceCapture.Start(map);
            lastCaptureFailure = null; lastCapturePath = traceCapture.Path;
            return "Thermal capture started: " + traceCapture.Path;
        }
        internal static string CaptureStop() {
            if (traceCapture == null) return "Thermal capture is not active";
            string path = traceCapture.Path;
            StopCapture("stopped");
            return "Thermal capture stopped: " + path + (lastCaptureFailure == null ? string.Empty : " (" + lastCaptureFailure + ")");
        }
        private static void StopCapture(string reason = "round-reset") {
            if (traceCapture == null) return;
            lastCapturePath = traceCapture.Path;
            if (traceCapture.Active) traceCapture.Complete(reason); else traceCapture.Dispose();
            lastCaptureFailure = traceCapture.Failure; traceCapture = null;
        }
        internal static void Step(float dt) {
            if (!Ready || !WeatherService.Active || !WeatherService.Synchronized) return;
            if (WeatherService.Authority && Enabled) {
                accumulator += dt * Catalog.TimeScale;
                while (accumulator >= .25f) { accumulator -= .25f; Advance(.25f); }
            }
            ThermalNetwork.Tick();
#if CLIENT
            ThermalClient.Step(dt);
#endif
        }
        private static void Advance(float dt) {
            step++;
            ThermalMap.Step(dt);
            removedItems.Clear();
            foreach (var pair in garments) {
                Item item = pair.Key; Garment garment = pair.Value;
                if (item.Removed) { removedItems.Add(item); continue; }
            }
            for (int i = 0; i < removedItems.Count; i++) garments.Remove(removedItems[i]);
            IReadOnlyList<Character> people = Humans.All;
            for (int i = 0; i < people.Count; i++) {
                Character c = people[i];
                if (c.Removed || c.IsDead || c.AnimController == null || c.CharacterHealth == null || c.Params.IsMachine) continue;
                ThermalGovernance rule = ThermalGovernanceRules.Decide(Enabled, c.GodMode, c.DisabledByEvent);
                if (rule != ThermalGovernance.Govern) {
                    if (subjects.Remove(c)) Release(c, rule == ThermalGovernance.ReleaseAll);
                    continue;
                }
                if (!subjects.TryGetValue(c, out Subject subject)) {
                    subject = new Subject(); subjects.Add(c, subject);
                    ClearLegacy(c);
                }
                if (subject.Quarantined) continue;
                try {
                    Advance(c, subject, dt);
                    subject.Faults = 0;
                } catch (Exception error) {
                    subject.Faults++;
                    ModLog.Error("Thermal step failed for " + c.Name + " (#" + c.ID + ", fault " + subject.Faults + "): " + error);
                    if (ThermalGovernanceRules.Quarantine(subject.Faults)) {
                        subject.Quarantined = true;
                        Release(c, false);
                        ModLog.Error("Thermal simulation withdrawn from " + c.Name + " for the rest of the round; its cold afflictions were cleared.");
                    }
                }
            }
            if (traceCapture != null && step % 4 == 0) {
                double captureTime = step * .25;
                ThermalMap.Capture(traceCapture, captureTime);
                traceCapture.Flush(captureTime);
                if (!traceCapture.Active) { lastCapturePath = traceCapture.Path; lastCaptureFailure = traceCapture.Failure; traceCapture.Dispose(); traceCapture = null; }
            }
            foreach (var pair in garments) {
                Item item = pair.Key; Garment garment = pair.Value;
                if (garment.Updated == step) continue;
                ThermalSample local = ThermalMap.Sample(item.WorldPosition); WeatherState weather = WeatherService.Current;
                for (int i = 0; i < ThermalBody.Regions; i++) garment.Wet[i] = ThermalBody.Moisture(garment.Wet[i], item.InWater ? 1 : 0, garment.Profile.Layers[i].Waterproof, local.Air, local.Inside ? .45f : weather.Humidity, local.Draft, dt);
            }
            removedPeople.Clear(); foreach (var pair in subjects) if (pair.Key.Removed || pair.Key.IsDead) removedPeople.Add(pair.Key);
            for (int i = 0; i < removedPeople.Count; i++) subjects.Remove(removedPeople[i]);
        }
        private static void Advance(Character c, Subject s, float dt) {
            ObserveAfflictions(c, s);
            Vector2 world = c.WorldPosition; WeatherState weather = WeatherService.Current;
            ThermalSample local = ThermalMap.Sample(world);
            bool indoors = local.Inside;
            float exposure = WeatherService.SkyAt(world);
            float immersion = 0;
            if (c.AnimController.InWater) immersion = c.CurrentHull == null ? 1 : Math.Clamp((c.CurrentHull.WorldSurface - (world.Y - 90)) / 180, 0, 1);
            float waterTemperature = indoors ? 0 : -.5f;
            if (immersion > 0 && c.CurrentHull != null && ThermalMap.Field != null) {
                float halfCell = ThermalMap.Field.Grid.Cell * WeatherService.UnitsPerMetre * .5f;
                ThermalSample submerged = ThermalMap.Sample(new Vector2(world.X, MathF.Min(world.Y, c.CurrentHull.WorldSurface - halfCell)));
                if (submerged.Water > 0) waterTemperature = submerged.Air;
                if (local.Water > 0 && immersion < 1) local = ThermalMap.Sample(new Vector2(world.X, c.CurrentHull.WorldSurface + halfCell));
            }
            float bathDepth = Bath.BathModule.Depth(world, out float bathCelsius);
            if (bathDepth > immersion) { immersion = bathDepth; waterTemperature = bathCelsius; }
            float air = local.Air;
            var environment = new ThermalEnvironment { Air = air, Radiant = indoors ? ThermalMap.RadiantAt(world) : air - 8 * (1 - weather.Cloud) * exposure,
                Water = waterTemperature, Wind = local.Inside ? local.Draft : .1f + weather.WindSpeed * MathF.Max(.05f, exposure),
                Humidity = indoors ? .45f : weather.Humidity, Immersion = immersion, Activity = Activity(c),
                Perfusion = Math.Clamp(1 - c.CharacterHealth.BloodlossAmount / 100, .1f, 1), Oxygen = Math.Clamp((c.CharacterHealth.OxygenAmount + 20) / 100, .1f, 1),
                Irradiance = Irradiance(c, world), RecoveryPace = Catalog.RecoveryPace };
            ThermalContact contact = default;
            for (int i = 0; i < s.Warmth.Length; i++) contact.Add(WarmthRegions[i], MathF.Min(80, s.Warmth[i] * .8f));
            contact.Add(ThermalRegion.Trunk, s.LegacyWarmth);
            contact.Add(PortableHeat(c, dt));
            environment.Contact = contact;
            Span<ThermalProtection> protection = stackalloc ThermalProtection[ThermalBody.Regions];
            protection.Clear();
            for (int slot = 0; slot < Slots.Length; slot++) {
                Item item = c.Inventory?.GetItemInLimbSlot(Slots[slot]); s.Worn[slot] = item;
                if (item == null || item.Removed) continue;
                bool duplicate = false; for (int n = 0; n < slot; n++) if (s.Worn[n] == item) { duplicate = true; break; }
                if (duplicate || !profiles.TryGetValue(item.Prefab.Identifier, out var equipped) || equipped.Slot != Slots[slot]) continue;
                ThermalGarment profile = equipped.Garment;
                if (!garments.TryGetValue(item, out Garment garment)) { garment = new Garment(profile); garments.Add(item, garment); }
                garment.Updated = step;
                for (int region = 0; region < ThermalBody.Regions; region++) {
                    ThermalLayer layer = profile.Layers[region];
                    if (layer.Coverage <= 0) continue;
                    float localWater = ThermalBody.Immersion(region, immersion);
                    garment.Wet[region] = ThermalBody.Moisture(garment.Wet[region], localWater, layer.Waterproof, air, environment.Humidity, environment.Wind, dt, environment.Irradiance.For(region));
                    protection[region].Add(layer.Clo, layer.Coverage, layer.Windproof, layer.Waterproof, garment.Wet[region], item.ConditionPercentage / 100);
                }
            }
            for (int i = 0; i < ThermalBody.Regions; i++) {
                if (s.Body.Core > 37.2f) s.SkinWet[i] = MathF.Min(1, s.SkinWet[i] + (s.Body.Core - 37.2f) * dt / 300);
                s.SkinWet[i] = ThermalBody.Moisture(s.SkinWet[i], ThermalBody.Immersion(i, immersion), protection[i].Waterproof, air, environment.Humidity, environment.Wind, dt, environment.Irradiance.For(i));
                protection[i].Wetness = MathF.Max(protection[i].Wetness, s.SkinWet[i]);
            }
            for (int i = 0; i < LimbTypes.Length; i++) {
                float present = s.Frost[i]?.Strength ?? 0;
                if (present + .01f < s.Applied[i]) {
                    float reduction = (s.Applied[i] - present) / 100;
                    s.Body.Treat(i, reduction);
                }
            }
            s.Body.Step(environment, protection, dt);
            double capturedRadiation = s.Body.LastBalance.SourceRadiationWatts;
            ThermalMap.BodyHeatAt(world, s.Body.EnvironmentWatts + capturedRadiation);
            Span<Vector2> receiverPoints = stackalloc Vector2[1 + ThermalBody.Regions * 2];
            int pointCount = RadiationPoints(c, receiverPoints);
            ThermalMap.CaptureRadiation(receiverPoints.Slice(0, pointCount), capturedRadiation);
            Apply(c, null, ref s.Cold, ColdId, s.Body.ColdStrength);
            for (int side = 0; side < 2; side++) {
                Limb hand = c.AnimController.GetLimb(LimbTypes[2 + side]), foot = c.AnimController.GetLimb(LimbTypes[4 + side]);
                if (hand != null) Apply(c, hand, ref s.Dexterity[side], DexterityId, (1 - s.Body.Dexterity) * 100);
                if (foot != null) Apply(c, foot, ref s.Gait[side], GaitId, (1 - s.Body.Gait) * 100);
            }
            Apply(c, null, ref s.Pain, PainId, s.Body.Pain * 100);
            Apply(c, null, ref s.Shock, ShockId, s.Body.ColdShock * 100);
            Apply(c, null, ref s.WetCold, WetColdId, s.Body.WetCold * 100);
            Apply(c, null, ref s.Chill, ChillId, s.Body.ChilledSkin);
            Apply(c, null, ref s.Heat, HeatId, MathF.Max(MathF.Max(0, s.Body.Core - 39) * 20, s.Body.HeatStress * 60));
            float injury = 0, skin = float.PositiveInfinity;
            for (int i = 0; i < ThermalBody.Regions; i++) { injury = MathF.Max(injury, s.Body.Injury[i]); skin = MathF.Min(skin, s.Body.Tissue[i].Temperature); }
            for (int i = 0; i < LimbTypes.Length; i++) {
                s.Applied[i] = s.Body.Injury[i] * 100;
                Limb limb = c.AnimController.GetLimb(LimbTypes[i]);
                if (limb != null) Apply(c, limb, ref s.Frost[i], FrostId, s.Applied[i]);
            }
            s.Reading = new ThermalReading { Character = c.ID, Sequence = ++sequence, Core = s.Body.Core, Skin = skin, Air = air, Wind = environment.Wind, Wetness = s.Body.Wetness, Reserve = s.Body.Reserve, Frostbite = injury, Dexterity = s.Body.Dexterity, Gait = s.Body.Gait, Pain = s.Body.Pain, ColdShock = s.Body.ColdShock, WetCold = s.Body.WetCold };
            s.ReadAt = Timing.TotalTime;
            if (traceCapture != null && traceCapture.Active && step % 4 == 0) {
                bool hasRoom = ThermalMap.RoomAt(world, out int room);
                traceCapture.Character(step * .25, c.ID, world.X / WeatherService.UnitsPerMetre, world.Y / WeatherService.UnitsPerMetre,
                    hasRoom ? room : -1, s.Body, environment);
            }
        }
        private static readonly LimbType[][] IrradiatedLimbs = {
            new[] { LimbType.Head }, new[] { LimbType.Torso, LimbType.Waist }, new[] { LimbType.LeftArm, LimbType.RightArm },
            new[] { LimbType.LeftHand, LimbType.RightHand }, new[] { LimbType.LeftLeg, LimbType.RightLeg }, new[] { LimbType.LeftFoot, LimbType.RightFoot } };
        private static int RadiationPoints(Character c, Span<Vector2> points) {
            int count = 1; points[0] = c.WorldPosition;
            for (int region = 0; region < IrradiatedLimbs.Length; region++) {
                LimbType[] types = IrradiatedLimbs[region];
                for (int i = 0; i < types.Length; i++) {
                    Limb limb = c.AnimController.GetLimb(types[i]);
                    if (limb != null && !limb.Removed) points[count++] = limb.WorldPosition;
                }
            }
            return count;
        }
        private static ThermalContact Irradiance(Character c, Vector2 world) {
            ThermalContact flux = default;
            if (!ThermalMap.AnySource(world)) return flux;
            for (int region = 0; region < IrradiatedLimbs.Length; region++) {
                float sum = 0; int count = 0;
                LimbType[] types = IrradiatedLimbs[region];
                for (int n = 0; n < types.Length; n++) {
                    Limb limb = c.AnimController.GetLimb(types[n]); if (limb == null || limb.Removed) continue;
                    sum += ThermalMap.RadiationAt(limb.WorldPosition); count++;
                }
                flux.Add((ThermalRegion)region, count == 0 ? ThermalMap.RadiationAt(world) : MathF.Min(ThermalBody.MaxFlux, sum / count));
            }
            return flux;
        }
        private static Item ChargedCell(Item container, int depth) {
            if (depth > 3 || container?.OwnInventory == null) return null;
            Inventory inventory = container.OwnInventory;
            for (int i = 0; i < inventory.Capacity; i++) {
                Item cell = inventory.GetItemAt(i);
                if (cell == null || cell.Removed) continue;
                if (cell.HasTag(BatteryTag) && cell.Condition > 0) return cell;
                Item nested = ChargedCell(cell, depth + 1);
                if (nested != null) return nested;
            }
            return null;
        }
        private static ThermalContact PortableHeat(Character c, float dt) {
            ThermalContact contact = default;
            if (c.Inventory == null) return contact;
            Item previous = null;
            for (int i = 0; i < HeatedSlots.Length; i++) {
                Item heater = c.Inventory.GetItemInLimbSlot(HeatedSlots[i]);
                if (heater == null || heater.Removed || heater.Condition <= 0 || heater == previous) continue;
                previous = heater;
                if (!string.Equals(heater.Prefab.Identifier.Value, Catalog.PortableIdentifier, StringComparison.OrdinalIgnoreCase)) continue;
                Item battery = ChargedCell(heater, 0);
                if (battery == null) continue;
                float condition = battery.Condition;
                float delivered = ThermalPower.Draw(ref condition, Catalog.PortableWatts, Catalog.BatteryJoulesPerCondition, dt);
                battery.Condition = condition;
                ThermalRegion region = HeatedSlots[i] is InvSlotType.RightHand or InvSlotType.LeftHand ? ThermalRegion.Hands : ThermalRegion.Trunk;
                contact.Add(region, delivered);
            }
            return contact;
        }
        private static float Activity(Character c) {
            if (c.IsUnconscious || c.AnimController.TargetMovement.LengthSquared() < .0025f) return 0;
            StaminaActivity activity = StaminaSim.Classify(c);
            float effort = StaminaSim.Exerting(c, activity) ? 1.5f : activity == StaminaActivity.Water ? .9f : .45f;
            float sinkage = activity == StaminaActivity.Ground ? WeatherService.Footing(c).Sinkage : 0;
            return MathF.Min(2, effort * (1 + Math.Clamp(sinkage / .3f, 0, 1)));
        }
        private static void Apply(Character c, Limb limb, ref Affliction cached, Identifier id, float strength) {
            if (cached == null) {
                if (strength < .01f) return;
                c.CharacterHealth.ApplyAffliction(limb, AfflictionPrefab.Prefabs[id].Instantiate(strength), allowStacking: false);
                foreach (var pair in c.CharacterHealth.afflictions) if (pair.Key.Identifier == id && (limb == null ? pair.Value == null : pair.Value == c.CharacterHealth.limbHealths[limb.HealthIndex])) { cached = pair.Key; break; }
            }
            if (cached != null) cached.Strength = Math.Clamp(strength, 0, 100);
        }
        private static void ObserveAfflictions(Character c, Subject s) {
            s.LegacyWarmth = 0;
            s.Cold = s.Chill = s.Heat = s.Pain = s.Shock = s.WetCold = null; Array.Clear(s.Dexterity, 0, s.Dexterity.Length); Array.Clear(s.Gait, 0, s.Gait.Length);
            Array.Clear(s.Frost, 0, s.Frost.Length); Array.Clear(s.Warmth, 0, s.Warmth.Length);
            Span<int> health = stackalloc int[LimbTypes.Length];
            for (int i = 0; i < health.Length; i++) health[i] = c.AnimController.GetLimb(LimbTypes[i])?.HealthIndex ?? -1;
            foreach (var pair in c.CharacterHealth.afflictions) {
                Identifier id = pair.Key.Identifier;
                if (id == DexterityId || id == GaitId) {
                    for (int side = 0; side < 2; side++) {
                        if (id == DexterityId && (uint)health[2 + side] < c.CharacterHealth.limbHealths.Count && pair.Value == c.CharacterHealth.limbHealths[health[2 + side]]) s.Dexterity[side] = pair.Key;
                        if (id == GaitId && (uint)health[4 + side] < c.CharacterHealth.limbHealths.Count && pair.Value == c.CharacterHealth.limbHealths[health[4 + side]]) s.Gait[side] = pair.Key;
                    }
                }
                else if (id == ColdId) s.Cold = pair.Key;
                else if (id == ChillId) s.Chill = pair.Key;
                else if (id == HeatId) s.Heat = pair.Key;
                else if (id == WarmId) {
                    if (pair.Value == null) s.Warmth[1] = MathF.Max(s.Warmth[1], pair.Key.Strength);
                    else for (int i = 0; i < health.Length; i++)
                        if ((uint)health[i] < c.CharacterHealth.limbHealths.Count && pair.Value == c.CharacterHealth.limbHealths[health[i]])
                            s.Warmth[i] = MathF.Max(s.Warmth[i], pair.Key.Strength);
                }
                else if (id == LegacyWarmId) s.LegacyWarmth = MathF.Max(s.LegacyWarmth, MathF.Min(80, pair.Key.Strength * .8f));
                else if (id == PainId) s.Pain = pair.Key;
                else if (id == ShockId) s.Shock = pair.Key;
                else if (id == WetColdId) s.WetCold = pair.Key;
                else if (id == FrostId) for (int i = 0; i < health.Length; i++) if ((uint)health[i] < c.CharacterHealth.limbHealths.Count && pair.Value == c.CharacterHealth.limbHealths[health[i]]) s.Frost[i] = pair.Key;
            }
        }
        private static void Release(Character c, bool includeInjury) {
            if (c?.CharacterHealth == null) return;
            foreach (var pair in c.CharacterHealth.afflictions) {
                Identifier id = pair.Key.Identifier;
                if (id == ColdId || id == ChillId || id == HeatId || id == DexterityId || id == GaitId || id == PainId || id == ShockId || id == WetColdId || includeInjury && id == FrostId) pair.Key.Strength = 0;
            }
        }
        private static void ClearLegacy(Character c) {
            foreach (Affliction a in c.CharacterHealth.GetAllAfflictions()) if (a.Identifier == "pp_hypothermia".ToIdentifier() || a.Identifier == "pp_frostbite".ToIdentifier() || a.Identifier == "pp_elevated_core_temp".ToIdentifier()) a.Strength = 0;
        }
        internal static bool TryRead(Character c, out ThermalReading reading) {
            if (Ready && Enabled && WeatherService.Active && c != null && !c.Removed && !c.IsDead && subjects.TryGetValue(c, out Subject subject) && !subject.Quarantined && subject.Faults == 0 && Timing.TotalTime >= subject.ReadAt && Timing.TotalTime - subject.ReadAt < 3) { reading = subject.Reading; return reading.Character == c.ID && reading.Character != 0; }
            reading = default; return false;
        }
        internal static void Dry(Character c) {
            if (!WeatherService.Authority || c == null || !subjects.TryGetValue(c, out Subject subject)) return;
            for (int i = 0; i < ThermalBody.Regions; i++) subject.SkinWet[i] *= .4f;
            for (int i = 0; i < subject.Worn.Length; i++) if (subject.Worn[i] is Item item && garments.TryGetValue(item, out Garment garment)) for (int n = 0; n < ThermalBody.Regions; n++) garment.Wet[n] *= .75f;
        }
    }
}
