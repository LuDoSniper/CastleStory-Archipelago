using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using BepInEx.Configuration;
using UnityEngine;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
        // Keep receipt and delivery separate. The Unity network SpawnUnit API
        // requires game-owned AssetKey, GameObject owner and Occupation.Type.
        // Claiming that simply increasing GeneralRules.bricktronCap creates
        // independent Crystal slots would be incorrect. Never consume a reward
        // unless native spawn + equipment assignment have succeeded.
        [Serializable] private sealed class BonusRewardTicket
        {
            public int index;
            public int item;
            public string state;
            public string note;
        }
        [Serializable] private sealed class BonusRewardJournal
        {
            public string token;
            public BonusRewardTicket[] tickets;
        }

        private readonly Dictionary<int, BonusRewardTicket> bonusRewards =
            new Dictionary<int, BonusRewardTicket>();
        private string activeBonusToken = "";
        private ConfigEntry<bool> nativeBonusSpawning;
        private ConfigEntry<bool> disableAutomaticUnitRewards;
        private ConfigEntry<bool> bonusQuotaCompensation;
        private MethodInfo nativeSpawnMethod;
        private object nativeSpawnHost;
        private object[] nativeSpawnPrototype;
        private bool bonusSpawnInProgress;
        private GameObject lastBonusSpawn;
        private float nextBonusAttempt;
        private readonly HashSet<int> trackedBonusInstanceIds = new HashSet<int>();

        private void InstallBonusSpawnHooks()
        {
            disableAutomaticUnitRewards = Config.Bind("Bonus Rewards",
                "DisableSeedUnitRewards", false,
                "Emergency opt-out for automatic native unit gifts enabled by " +
                "new Archipelago seeds. Existing seeds remain unchanged.");
            nativeBonusSpawning = Config.Bind("Bonus Rewards", "EnableNativeBonusSpawns", false,
                "DANGEROUS TEST ONLY: allow native SpawnUnit replay with recorded local ownership. " +
                "Use a disposable SOLO save. A missing template leaves the reward pending.");
            bonusQuotaCompensation = Config.Bind("Bonus Rewards", "ExperimentalExcludeBonusFromCrystalCount", false,
                "DANGEROUS TEST ONLY: subtract tagged, living AP units from the home-nest " +
                "character count. Other game systems may still count them.");
            try
            {
                Type type = AccessTools.TypeByName("Brix.Game.Network.UNetProjectCmd");
                nativeSpawnMethod = type == null ? null :
                    AccessTools.Method(type, "SpawnUnit");
                if (nativeSpawnMethod == null)
                {
                    Logger.LogWarning("[AP BONUS] Native SpawnUnit not found.");
                    return;
                }
                var parameters = nativeSpawnMethod.GetParameters();
                if (parameters.Length != 6 ||
                    parameters[1].ParameterType != typeof(Vector3) ||
                    parameters[2].ParameterType != typeof(Quaternion) ||
                    parameters[3].ParameterType != typeof(GameObject) ||
                    parameters[5].ParameterType != typeof(bool) ||
                    nativeSpawnMethod.ReturnType != typeof(GameObject))
                {
                    nativeSpawnMethod = null;
                    Logger.LogWarning("[AP BONUS] Native SpawnUnit signature differs. No rewards spawned.");
                    return;
                }
                harmony.Patch(nativeSpawnMethod, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CastleStoryArchipelagoPlugin), "AfterNativeUnitSpawn")));
                Logger.LogInfo("[AP BONUS] SpawnUnit hook attached; legacy opt-in=" +
                    nativeBonusSpawning.Value +
                    " (new-seed native rewards read from Archipelago slot data).");
            }
            catch (Exception error)
            {
                nativeSpawnMethod = null;
                Logger.LogWarning("[AP BONUS] Hook unavailable: " + error);
            }
        }

        // Observe vanilla spawns. The callback has game-owned prefab keys and a
        // validated owning GameObject, which we cannot safely invent from strings.
        private static void AfterNativeUnitSpawn(object __instance, object[] __args,
                                                  GameObject __result)
        {
            if (Instance != null)
                Instance.ObserveNativeUnitSpawn(__instance, __args, __result);
        }

        private void ObserveNativeUnitSpawn(object sender, object[] parameters, GameObject unit)
        {
            if (unit == null) return;
            if (bonusSpawnInProgress)
            {
                lastBonusSpawn = unit;
                trackedBonusInstanceIds.Add(unit.GetInstanceID());
                Logger.LogInfo("[AP BONUS] Native SpawnUnit returned AP bonus unit instance " +
                    unit.GetInstanceID());
                return;
            }
            if (parameters == null || parameters.Length != 6 ||
                parameters[3] == null || !(parameters[3] is GameObject) ||
                !IsSeedGameplayActive())
                return;
            if (!IsLocalOwnedUnit(unit))
                return;
            nativeSpawnHost = sender;
            nativeSpawnPrototype = (object[])parameters.Clone();
            Logger.LogInfo("[AP BONUS] Cached verified local native spawn template; " +
                "asset=" + (parameters[0] == null ? "<null>" : parameters[0].ToString()) +
                ", role=" + (parameters[4] == null ? "<null>" : parameters[4].ToString()));
        }

        private bool IsLocalOwnedUnit(GameObject gameObject)
        {
            try
            {
                // Unity may attach Affiliation to the unit's child visual/
                // ownership object rather than to its root GameObject.
                foreach (Component part in gameObject.GetComponentsInChildren<Component>(true))
                {
                    if (part == null || !part.GetType().Name.Contains("Affiliation")) continue;
                    var factionProperty = AccessTools.Property(part.GetType(), "Faction");
                    object faction = factionProperty == null ? null :
                        factionProperty.GetValue(part, null);
                    if (faction == null) return false;
                    var owned = AccessTools.Property(faction.GetType(), "IsOwnedByMe");
                    return owned != null && Convert.ToBoolean(owned.GetValue(faction, null));
                }
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP BONUS] Could not validate local unit: " + error.Message);
            }
            return false;
        }

        private static bool IsBonusUnitItem(int id)
        {
            // Bricktron Reinforcement or equipped Worker/Archer/Knight/Alchemist.
            return id == ItemBase + 4 ||
                id >= ItemBase + 8 && id <= ItemBase + 11;
        }

        private string BonusOccupation(int item)
        {
            if (item == ItemBase + 4 || item == ItemBase + 8) return "Worker";
            if (item == ItemBase + 9) return "Archer";
            if (item == ItemBase + 10) return "Knight";
            if (item == ItemBase + 11) return "Alchemist";
            return null;
        }

        private bool BonusUnlockReady(int item)
        {
            if (!gameplayRequireUnlocks) return true;
            int required = item == ItemBase + 9 ? ItemBase + 1 :
                item == ItemBase + 10 ? ItemBase + 2 :
                item == ItemBase + 11 ? ItemBase + 7 : 0;
            return required == 0 ||
                (inventory.ContainsKey(required) && inventory[required] > 0);
        }

        private void ClearNativeBonusSceneTemplate()
        {
            // Unity GameObjects and method hosts are scene-owned. Reusing a
            // template captured before Save & Quit could address a destroyed
            // owner or spawn at a stale coordinate in a different map.
            nativeSpawnHost = null;
            nativeSpawnPrototype = null;
            lastBonusSpawn = null;
            trackedBonusInstanceIds.Clear();
        }

        private string BonusJournalPath()
        {
            // A separate file per slot seed prevents accidental cross-seed grants.
            return Path.Combine(directory, "bonus_rewards_" + token + ".json");
        }

        private void ResetBonusRewards(string newToken)
        {
            bonusRewards.Clear();
            activeBonusToken = newToken;
            if (String.IsNullOrEmpty(newToken)) return;
            try
            {
                string path = BonusJournalPath();
                if (!File.Exists(path)) return;
                var saved = JsonUtility.FromJson<BonusRewardJournal>(
                    File.ReadAllText(path));
                if (saved == null || saved.token != newToken ||
                    saved.tickets == null) return;
                foreach (var ticket in saved.tickets)
                {
                    if (ticket == null || ticket.index < 0 ||
                        !IsBonusUnitItem(ticket.item)) continue;
                    bonusRewards[ticket.index] = ticket;
                }
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP BONUS] Could not load local journal: " + error);
            }
        }

        private bool SaveBonusRewards()
        {
            if (String.IsNullOrEmpty(token) || activeBonusToken != token) return false;
            try
            {
                string path = BonusJournalPath();
                var tickets = bonusRewards.Values.OrderBy(t => t.index).ToArray();
                WriteCheckLedgerFile(path, JsonUtility.ToJson(new BonusRewardJournal {
                    token = token, tickets = tickets
                }));
                return true;
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP BONUS] Cannot save journal: " + error);
                return false;
            }
        }

        private void ReconcileBonusRewards(ItemEntry[] serverItems)
        {
            if (serverItems == null || String.IsNullOrEmpty(token) ||
                activeBonusToken != token) return;
            bool dirty = false;
            var received = new HashSet<int>();
            foreach (var item in serverItems)
            {
                if (item == null || !IsBonusUnitItem(item.item) ||
                    item.index < 0 || !received.Add(item.index)) continue;
                BonusRewardTicket saved;
                if (!bonusRewards.TryGetValue(item.index, out saved))
                {
                    bonusRewards[item.index] = new BonusRewardTicket {
                        index = item.index, item = item.item, state = "pending",
                        note = "Awaiting verified local native unit spawn."
                    };
                    dirty = true;
                    Logger.LogInfo("[AP BONUS] New equipped Bricktron reward #" +
                        item.index + " item=" + item.item + " queued.");
                }
                else if (saved.item != item.item)
                {
                    // Inventory index unexpectedly changed: never duplicate a grant.
                    saved.state = "blocked";
                    saved.note = "AP inventory index mismatch; manual review required.";
                    dirty = true;
                }
            }
            if (dirty) SaveBonusRewards();
        }

        private int PendingBonusCount()
        {
            return bonusRewards.Values.Count(t => t.state == "pending");
        }

        private void ProcessPendingNativeBonus()
        {
            // Native reward spawning is enabled ONLY by a new APWorld's
            // gameplay_rules (plus an emergency opt-out), or by the player's
            // explicit old experimental flag. Never change ordinary saves.
            bool enabledForSeed = gameplayUnitRewardsEnabled &&
                (disableAutomaticUnitRewards == null ||
                 !disableAutomaticUnitRewards.Value);
            bool experimental = nativeBonusSpawning != null &&
                nativeBonusSpawning.Value;
            if ((!enabledForSeed && !experimental) ||
                !bridgeConnected || !IsSeedGameplayActive() ||
                Time.realtimeSinceStartup < nextBonusAttempt) return;
            nextBonusAttempt = Time.realtimeSinceStartup + 2f;
            if (nativeSpawnMethod == null || nativeSpawnHost == null ||
                nativeSpawnPrototype == null) return;
            GameObject templateOwner = nativeSpawnPrototype[3] as GameObject;
            if (templateOwner == null) return; // destroyed Unity object
            if (nativeSpawnHost is UnityEngine.Object &&
                (UnityEngine.Object)nativeSpawnHost == null) return;
            var ticket = bonusRewards.Values.Where(x => x.state == "pending" &&
                BonusUnlockReady(x.item)).OrderBy(x => x.index).FirstOrDefault();
            if (ticket == null) return;

            var proto = (object[])nativeSpawnPrototype.Clone();
            string desired = BonusOccupation(ticket.item);
            if (String.IsNullOrEmpty(desired)) return;
            Type roleType = nativeSpawnMethod.GetParameters()[4].ParameterType;
            if (!roleType.IsEnum)
            {
                ticket.note = "Native occupation parameter was not an enum.";
                SaveBonusRewards();
                return;
            }
            string role = Enum.GetNames(roleType).FirstOrDefault(x =>
                x.Equals(desired, StringComparison.OrdinalIgnoreCase));
            if (String.IsNullOrEmpty(role))
            {
                ticket.note = "Verified native occupation not available: " + desired;
                SaveBonusRewards();
                return;
            }
            proto[4] = Enum.Parse(roleType, role);
            if (proto[1] is Vector3)
            {
                Vector3 p = (Vector3)proto[1];
                // Offset from a VANILLA locally owned unit; never guess the
                // game's own asset, network owner, or initial scene position.
                proto[1] = new Vector3(p.x + 1.5f, p.y + .2f, p.z + 1.5f);
            }
            ticket.state = "spawning";
            ticket.note = "Native SpawnUnit invoked. Do not replay after a crash.";
            // Never spawn if the intent cannot first be safely persisted.
            // Otherwise a restart could replay a gift that already existed.
            if (!SaveBonusRewards())
            {
                ticket.state = "pending";
                ticket.note = "Reward journal unavailable; no unit spawned.";
                return;
            }
            try
            {
                bonusSpawnInProgress = true;
                lastBonusSpawn = null;
                object result = nativeSpawnMethod.Invoke(nativeSpawnHost, proto);
                GameObject spawned = lastBonusSpawn ?? result as GameObject;
                if (spawned != null && IsLocalOwnedUnit(spawned))
                {
                    ticket.state = "delivered";
                    ticket.note = "Native game-owned unit spawned. " +
                        "Counts toward Castle Story's normal unit cap.";
                    Logger.LogInfo("[AP REWARD DELIVERED] index=" + ticket.index +
                        " item=" + ticket.item + " role=" + desired +
                        " instance=" + spawned.GetInstanceID());
                }
                else
                {
                    ticket.state = "review";
                    ticket.note = "Native spawn not confirmed. Never retry blindly.";
                    Logger.LogWarning("[AP REWARD REVIEW] index=" + ticket.index +
                        " no owned unit returned.");
                }
            }
            catch (Exception error)
            {
                ticket.state = "review";
                ticket.note = "Native spawn error: " + error.GetType().Name;
                Logger.LogError("[AP REWARD REVIEW] Spawn error #" +
                    ticket.index + ": " + error);
            }
            finally
            {
                bonusSpawnInProgress = false;
                SaveBonusRewards();
            }
        }

        private void DumpBonusRewardQueue()
        {
            Logger.LogInfo("[AP BONUS] F10 queue; token=" + token +
                " pending=" + PendingBonusCount());
            foreach (var ticket in bonusRewards.Values.OrderBy(x => x.index))
                Logger.LogInfo("[AP BONUS] index=" + ticket.index +
                    " item=" + ticket.item + " state=" + ticket.state +
                    " unlockReady=" + BonusUnlockReady(ticket.item) +
                    " note=" + ticket.note);
            Logger.LogInfo("[AP GAMEPLAY] seed-native rewards=" +
                gameplayUnitRewardsEnabled + " emergencyDisabled=" +
                (disableAutomaticUnitRewards != null && disableAutomaticUnitRewards.Value) +
                " gatedEquippedRewards=" + gameplayRequireUnlocks);
            Logger.LogInfo("[AP BONUS] Native template=" +
                (nativeSpawnPrototype == null ? "not observed" : "observed") +
                "; native spawning enabled=" +
                (nativeBonusSpawning != null && nativeBonusSpawning.Value) +
                "; tracked in this match=" + trackedBonusInstanceIds.Count);
            if (nativeSpawnMethod != null)
            {
                Type roleType = nativeSpawnMethod.GetParameters()[4].ParameterType;
                Logger.LogInfo("[AP BONUS] Native method " +
                    nativeSpawnMethod.ToString() + " roles=" +
                    (roleType.IsEnum ? String.Join(",", Enum.GetNames(roleType)) :
                     "unsupported occupation type"));
            }
        }
    }
}
