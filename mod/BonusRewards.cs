using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private static bool IsBonusUnitItem(int id)
        {
            // Equipped Worker / Archer / Knight / Alchemist.
            return id >= ItemBase + 8 && id <= ItemBase + 11;
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

        private void SaveBonusRewards()
        {
            if (String.IsNullOrEmpty(token) || activeBonusToken != token) return;
            try
            {
                string path = BonusJournalPath();
                string temporary = path + ".tmp";
                var tickets = bonusRewards.Values.OrderBy(t => t.index).ToArray();
                File.WriteAllText(temporary, JsonUtility.ToJson(new BonusRewardJournal {
                    token = token, tickets = tickets
                }));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP BONUS] Cannot save journal: " + error);
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
                        note = "Awaiting native cap-neutral spawn adapter."
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

        private void DumpBonusRewardQueue()
        {
            Logger.LogInfo("[AP BONUS] F10 queue; token=" + token +
                " pending=" + PendingBonusCount());
            foreach (var ticket in bonusRewards.Values.OrderBy(x => x.index))
                Logger.LogInfo("[AP BONUS] index=" + ticket.index +
                    " item=" + ticket.item + " state=" + ticket.state +
                    " note=" + ticket.note);
            Logger.LogInfo("[AP BONUS] GeneralRules.bricktronCap is NOT modified. " +
                "Native SpawnUnit/kit assignment remains under research.");
        }
    }
}
