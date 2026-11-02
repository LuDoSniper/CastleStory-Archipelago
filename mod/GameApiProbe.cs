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
            Logger.LogInfo("[AP API PROBE] Finished. Future hooks must be validated in game.");
        }
    }
}
