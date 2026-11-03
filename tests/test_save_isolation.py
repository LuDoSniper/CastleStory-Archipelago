"""Regression checks for native save tagging and safe save-list isolation.

Castle Story's actual Load screen requires a runtime integration test. These
tests cover the sidecar/data protection contract and the native hook wiring.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SAVE = (ROOT / "mod/SaveIsolation.cs").read_text(encoding="utf-8")
MENU = (ROOT / "mod/NativeMenuHooks.cs").read_text(encoding="utf-8")
PLUGIN = (ROOT / "mod/CastleStoryArchipelagoPlugin.cs").read_text(encoding="utf-8")
UI = (ROOT / "mod/NativeArchipelagoIntegration.cs").read_text(encoding="utf-8")


class SaveIsolationTests(unittest.TestCase):
    def test_uses_external_tags_and_does_not_modify_game_saves(self):
        self.assertIn('save_tags.json', SAVE)
        self.assertIn('new [] { "Saves", "Save", "Sauvegarde" }', SAVE)
        self.assertIn('Path.Combine(Paths.GameRootPath, "Info")', SAVE)
        self.assertIn('File.WriteAllText(temp, JsonUtility.ToJson(new ApSaveRegistry', SAVE)
        self.assertNotIn('File.WriteAllText(Path.Combine(apSaveRoot', SAVE)
        self.assertNotIn('File.Delete(Path.Combine(apSaveRoot', SAVE)
        self.assertNotIn('Directory.Move(', SAVE)
        self.assertNotIn('File.Move(folder', SAVE)

    def test_tags_new_folders_only_after_validated_seed_start(self):
        self.assertIn('PrepareNativeSaveTracking();', MENU)
        self.assertIn('startedMission = matching.id;', MENU)
        self.assertIn('if (!knownSaveFolders.Add(name)) continue;', SAVE)
        self.assertIn('if (Directory.GetFiles(folder).Length == 0) continue;', SAVE)
        self.assertIn('if (!finishingRun && !IsSeedGameplayActive()) return;', SAVE)
        self.assertIn('ScanNewNativeSaves(true)', PLUGIN)
        self.assertIn('token = token, seed = seed,', SAVE)
        self.assertIn('slot = loginSlot, mode = mode,', SAVE)
        self.assertIn('mission = startedMission,', SAVE)
        self.assertIn('map_id = active == null ? "" : MissionMapId(active)', SAVE)
        self.assertIn('!String.Equals(tag.map_id, MissionMapId(mission)', SAVE)
        self.assertIn('InitializeApSaveRegistry();', PLUGIN)
        self.assertIn('UpdateSaveIsolation();', PLUGIN)

    def test_connection_scopes_archipelago_saves_and_vanilla_saves(self):
        self.assertIn('if (bridgeConnected)', SAVE)
        self.assertIn('String.Equals(tag.token, token', SAVE)
        self.assertIn('return !tagged;', SAVE)
        self.assertIn('ScheduleNativeSaveFilter(name);', MENU)
        self.assertIn('FilterNativeSaveChoices();', SAVE)
        self.assertIn('FindNativeSaveCardRoot(label, known)', SAVE)

    def test_never_hide_entire_load_list_or_rewrite_corrupt_metadata(self):
        self.assertIn('if (folderCaptions > 1) break;', SAVE)
        self.assertIn('if (HasNativeInteractiveSurface(node))', SAVE)
        self.assertIn('saveRegistryWritable = false;', SAVE)
        self.assertIn('if (!saveRegistryWritable) return;', SAVE)
        self.assertIn('.archipelago-save-tags.json', SAVE)
        self.assertIn('MergeSaveTagsFrom(SaveTagsMirrorPath, false);', SAVE)
        self.assertIn('Only load-oriented menus', SAVE)

    def test_load_guard_applies_only_to_known_native_save_selections(self):
        self.assertIn('BeforeNativeLoadConfirmed', SAVE)
        self.assertIn('ValidateNativeLoad(__instance)', SAVE)
        self.assertIn('OpenSelf hooks=', SAVE)
        self.assertIn('ObserveNativeLoadLibraryOpened', SAVE)
        self.assertIn('ValidateNativeLoadStarter(menuInstance)', MENU)
        self.assertIn('NativeSaveFolderFromHeightmap(selectedMap)', SAVE)
        self.assertIn('return !bridgeConnected;', SAVE)
        self.assertIn('SelectMission(mission);', SAVE)
        self.assertIn('startedMission = mission.id;', SAVE)
        self.assertIn('campaign.missions.FirstOrDefault(', SAVE)
        self.assertIn('Rejected nonmatching or untagged', SAVE)

    def test_native_loads_do_not_hit_new_game_map_guard(self):
        self.assertIn('menuInstance.GetType().Name.IndexOf("LoadGameStarter"', MENU)
        self.assertIn('return ValidateNativeLoadStarter(menuInstance);', MENU)
        self.assertIn('const string marker = "/Info/Saves/";', SAVE)
        self.assertIn('SelectMission(mission);', SAVE)
        self.assertIn('startedMission = mission.id;', SAVE)
        self.assertIn('PrepareNativeSaveTracking();', SAVE)

    def test_reactivated_cards_are_hidden_again_and_title_order_is_stable(self):
        self.assertIn('if (hiddenNativeEntries.ContainsKey(button))', UI)
        self.assertIn('if (button.activeSelf)', UI)
        self.assertIn('titleArchipelagoControl.SetSiblingIndex(', UI)
        self.assertIn('MaintainTitleButtonSpacing();', PLUGIN)
        self.assertIn('ScheduleNativeSaveFilter("native title UI refreshed")', UI)

    def test_play_menu_never_uses_destructive_unknown_control_whitelist(self):
        self.assertIn('case "charger partie":', UI)
        self.assertIn('if (allowedPlay) continue;', UI)
        self.assertIn('!IsForbiddenPlaySubmenuEntry(value)', UI)
        self.assertIn('CountDistinctNativeChoices(card.transform) > 1', UI)
        self.assertNotIn('if (inPlaySubmenu && !allowedPlay)\n                    forbidden = true;', UI)

    def test_title_menu_spacing_uses_world_rect_corners_and_primary_style(self):
        self.assertIn('MaintainTitleButtonSpacing();', UI)
        self.assertIn('MoveRectVerticallyToTop(titleOptionsControl', UI)
        self.assertIn('titleArchipelagoControl.GetWorldCorners', UI)
        self.assertIn('GetUiProperty(source, "sprite")', UI)
        self.assertIn('AP_PrimaryAccent', UI)
        self.assertIn('AP_PrimaryBody', UI)


if __name__ == "__main__":
    unittest.main()
