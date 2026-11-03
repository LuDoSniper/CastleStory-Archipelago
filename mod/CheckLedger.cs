using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
        // An AP slot is one logical run even after Unity or the bridge restarts.
        // The existing checks.json stays as the bridge's live inbox, while
        // checks_by_run retains every run independently across slot switches.
        private string CheckLedgerPath(string ledgerToken)
        {
            if (String.IsNullOrEmpty(ledgerToken) || ledgerToken.Length != 24 ||
                ledgerToken.Any(c => !((c >= '0' && c <= '9') ||
                                        (c >= 'a' && c <= 'f'))))
                return null;
            return Path.Combine(Path.Combine(directory, "checks_by_run"),
                ledgerToken + ".json");
        }

        private CheckFile ReadCheckLedgerFile(string path, string expectedToken,
                                              string expectedMode)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var value = JsonUtility.FromJson<CheckFile>(
                    File.ReadAllText(path).TrimStart('\uFEFF', ' ', '\r', '\n', '\t'));
                if (value == null || value.token != expectedToken ||
                    value.mode != expectedMode)
                    return null;
                return value;
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP CHECK LEDGER] Unable to read " +
                    Path.GetFileName(path) + ": " + error.GetType().Name);
                return null;
            }
        }

        private void PreservePreviousCheckLedger()
        {
            // Migrate the last v0.7.x checks.json on the first run switch.
            // Never overwrite an existing per-run record with an older copy.
            string archive = CheckLedgerPath(token);
            if (String.IsNullOrEmpty(archive) || File.Exists(archive)) return;
            CheckFile previous = ReadCheckLedgerFile(
                Path.Combine(directory, "checks.json"), token, mode);
            if (previous == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(archive));
                File.WriteAllText(archive, JsonUtility.ToJson(previous));
                Logger.LogInfo("[AP CHECK LEDGER] Migrated previous run checks.");
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP CHECK LEDGER] Previous run backup failed: " +
                    error.GetType().Name);
            }
        }

        private void RestoreCheckLedgerForCurrentRun()
        {
            var paths = new [] { CheckLedgerPath(token),
                Path.Combine(directory, "checks.json") };
            int imported = 0;
            foreach (string path in paths)
            {
                CheckFile prior = ReadCheckLedgerFile(path, token, mode);
                if (prior == null) continue;
                if (prior.location_ids != null)
                    foreach (int id in prior.location_ids)
                        if (ValidCheck(mode, id, goalWave, shardGoal) &&
                            checkedIds.Add(id)) imported++;
                if (prior.captured_keys != null)
                    foreach (string key in prior.captured_keys)
                        if (!String.IsNullOrEmpty(key)) capturedKeys.Add(key);
                if (prior.completed_missions != null)
                    foreach (string mission in prior.completed_missions)
                        if (!String.IsNullOrEmpty(mission))
                            conqueredMissions.Add(mission);
            }
            Logger.LogInfo("[AP CHECK LEDGER] seed=" + seed +
                " restored=" + imported + " total=" + checkedIds.Count +
                " mode=" + mode);
        }

        private void WriteCheckLedgerFile(string path, string contents)
        {
            // .NET 3.5 on the user's Windows install rejected File.Replace
            // with a null backup path. Always provide a real backup and have
            // a copy fallback. The bridge retries malformed partial reads.
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }
            try
            {
                File.Replace(temp, path, path + ".bak");
                return;
            }
            catch (Exception error)
            {
                if (!File.Exists(temp))
                {
                    if (File.ReadAllText(path) == contents) return;
                    throw;
                }
                Logger.LogWarning("[AP CHECK LEDGER] Atomic replace unavailable for " +
                    Path.GetFileName(path) + " (" + error.GetType().Name +
                    "); using compatible backed-up copy.");
            }
            File.Copy(path, path + ".bak", true);
            File.Copy(temp, path, true);
            File.Delete(temp);
        }
    }
}
