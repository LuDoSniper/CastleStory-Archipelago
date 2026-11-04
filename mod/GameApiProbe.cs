using System;
using System.Reflection;
using HarmonyLib;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
        // Read-only reflection; does NOT invoke game methods or touch game state.
        // Helps find actual menu, map and spawn APIs without shipping game DLLs.
        private void ProbeGameApis()
        {
            string[] targets = {
                "MainMenuViewController", "MapHandler", "GameManager",
                "Brix.Game.StatsTracker", "CrystalDriver", "Bricktron"
            };
            Logger.LogInfo("[AP API PROBE] Unity read-only reflection started. No action executed.");
            foreach (string name in targets)
            {
                try
                {
                    Type type = AccessTools.TypeByName(name);
                    if (type == null)
                    {
                        Logger.LogInfo("[AP API PROBE] Type not found: " + name);
                        continue;
                    }
                    Logger.LogInfo("[AP API PROBE] Type " + type.FullName);
                    var methods = type.GetMethods(BindingFlags.Instance |
                        BindingFlags.Static | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    int count = 0;
                    foreach (var method in methods)
                    {
                        string label = method.Name.ToLowerInvariant();
                        if (!(label.Contains("map") || label.Contains("game") ||
                              label.Contains("start") || label.Contains("spawn") ||
                              label.Contains("unit") || label.Contains("bricktron") ||
                              label.Contains("crystal") || label.Contains("cap") ||
                              label.Contains("menu") || label.Contains("load")))
                            continue;
                        Logger.LogInfo("[AP API PROBE] method " + method.ToString());
                        if (++count >= 35) break;
                    }
                    int fields = 0;
                    foreach (var field in type.GetFields(BindingFlags.Instance |
                        BindingFlags.Static | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        string label = field.Name.ToLowerInvariant();
                        if (!(label.Contains("map") || label.Contains("spawn") ||
                              label.Contains("cap") || label.Contains("bricktron") ||
                              label.Contains("menu"))) continue;
                        Logger.LogInfo("[AP API PROBE] field " +
                            field.FieldType.FullName + " " + field.Name);
                        if (++fields >= 18) break;
                    }
                }
                catch (Exception error)
                {
                    Logger.LogWarning("[AP API PROBE] " + name + ": " + error.Message);
                }
            }
            ProbeNativeArchipelagoUi();
            ProbeNativeGameplayEffects();
            Logger.LogInfo("[AP API PROBE] Finished. Future hooks must be validated in game.");
        }
        private void ProbeNativeGameplayEffects()
        {
            // F9-only, read-only inventory of the *real loaded game assembly*.
            // This is for resource/build/equip adapters in the next iteration.
            // Do not infer methods from display labels or invoke anything.
            try
            {
                Type anchor = AccessTools.TypeByName("Brix.Game.Network.UNetProjectCmd");
                if (anchor == null) return;
                Type[] types;
                try { types = anchor.Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException partial)
                { types = partial.Types; }
                int logged = 0;
                foreach (Type type in types)
                {
                    if (type == null || logged >= 36) break;
                    string name = type.FullName ?? "";
                    if (!name.StartsWith("Brix.", StringComparison.Ordinal) &&
                        name.IndexOf("Crystal", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    string lower = name.ToLowerInvariant();
                    if (!lower.Contains("resource") && !lower.Contains("stock") &&
                        !lower.Contains("inventory") && !lower.Contains("occupation") &&
                        !lower.Contains("equipment") && !lower.Contains("bricktron") &&
                        !lower.Contains("crystal") && !lower.Contains("unlock") &&
                        !lower.Contains("build") && !lower.Contains("wave"))
                        continue;
                    logged++;
                    Logger.LogInfo("[AP GAMEPLAY API] type " + name);
                    int methods = 0;
                    foreach (MethodInfo method in type.GetMethods(
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly))
                    {
                        string action = method.Name.ToLowerInvariant();
                        if (!action.Contains("add") && !action.Contains("give") &&
                            !action.Contains("set") && !action.Contains("equip") &&
                            !action.Contains("spawn") && !action.Contains("resource") &&
                            !action.Contains("build") && !action.Contains("wave") &&
                            !action.Contains("unlock") && !action.Contains("consume"))
                            continue;
                        Logger.LogInfo("[AP GAMEPLAY API] method " + method);
                        if (++methods >= 12) break;
                    }
                }
                Logger.LogInfo("[AP GAMEPLAY API] read-only scan types=" + logged +
                    " (F9). No gameplay state changed.");
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP GAMEPLAY API] Probe failed: " +
                    error.GetType().Name);
            }
        }
    }
}
