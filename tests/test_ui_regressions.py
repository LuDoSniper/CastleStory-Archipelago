from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
UI = ROOT / "mod/ArchipelagoMenu.cs"
NATIVE = ROOT / "mod/NativeMenuHooks.cs"
INTEGRATION = ROOT / "mod/NativeArchipelagoIntegration.cs"
PLUGIN = ROOT / "mod/CastleStoryArchipelagoPlugin.cs"


class NativeUiIntegrationTests(unittest.TestCase):
    def test_main_menu_uses_native_play_button_not_floating_logo(self):
        ui = UI.read_text(encoding="utf-8")
        native = INTEGRATION.read_text(encoding="utf-8")
        plugin = PLUGIN.read_text(encoding="utf-8")
        self.assertNotIn("                DrawBrandingButton();", ui)
        self.assertIn("Archipelago_NativeMainMenuEntry", native)
        self.assertIn("copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1)", native)
        self.assertIn('SetNativeUiLabel(copy, "Archipelago")', native)
        self.assertIn("InstallNativeButtonListener", native)
        self.assertIn("clickProperty.SetValue(clonedButton, click", native)
        self.assertIn("if (Input.GetKeyDown(KeyCode.F7))", plugin)

    def test_native_seed_mode_map_restrictions_and_restore(self):
        code = INTEGRATION.read_text(encoding="utf-8")
        self.assertIn("IsSandboxLabel(label)", code)
        self.assertIn('mode != "Hybrid" && namedMode != mode', code)
        self.assertIn("!AllowedSeedMap(mapId, selectedNativeMode)", code)
        self.assertIn("nativeMapLabels.TryGetValue", code)
        self.assertIn("private void RestoreNativeEntries()", code)
        self.assertIn("if (hiddenNativeEntries.Count > 0) RestoreNativeEntries();", code)
        self.assertIn("if (lastNativeFilterKey != filterKey)", code)
        self.assertIn("FindPlayMenuRoot(controls, labels)", code)
        self.assertIn("SelectedNativeGamemode()", code)
        self.assertIn("RestoreNativeMenuLayout()", code)
        self.assertIn("disconnectPending || String.IsNullOrEmpty(token)", code)

    def test_native_ui_filter_is_event_driven_and_bounded(self):
        code = INTEGRATION.read_text(encoding="utf-8")
        hooks = NATIVE.read_text(encoding="utf-8")
        self.assertIn("pendingFilterPasses = 3;", code)
        self.assertIn("pendingFilterPasses > 0 && now >= nextFilterPassAt)", code)
        update = code.split("private void UpdateNativeArchipelagoIntegration()", 1)[1]
        update = update.split("private void DrawArchipelagoIntroCredit()", 1)[0]
        self.assertNotIn("RefreshNativeSeedMenu();\n        }", update)
        self.assertIn('System.Diagnostics.Stopwatch.StartNew()', code)
        self.assertIn("[AP UI PERF]", code)
        self.assertIn('InstallNativeMenuLifecycleHooks();', hooks)
        self.assertIn('ObserveAnyMenuOpened(object __instance)', hooks)
        self.assertIn('ScheduleNativeUiFilter("native starter fallback")', hooks)
        self.assertIn('ScheduleNativeUiFilter("native library OpenSelf fallback")', hooks)
        self.assertIn("OnArchipelagoSceneChanged(", code)
        self.assertIn("activeSceneChanged", PLUGIN.read_text(encoding="utf-8"))

    def test_map_catalog_is_not_rewritten_for_each_card(self):
        native = NATIVE.read_text(encoding="utf-8")
        plugin = PLUGIN.read_text(encoding="utf-8")
        section = native.split("private void RegisterAvailableMap(", 1)[1]
        section = section.split("private void TryDiscoverNativeMaps(", 1)[0]
        self.assertNotIn("SaveObservedMapIds(", section)
        self.assertIn("nativeMapCatalogSaveAt = Time.realtimeSinceStartup + 1.5f;", section)
        self.assertIn("private void FlushNativeMapCatalogIfIdle()", native)
        self.assertIn("FlushNativeMapCatalogIfIdle();", plugin)

    def test_title_button_and_credit_never_use_map_screen_play(self):
        ui = INTEGRATION.read_text(encoding="utf-8")
        self.assertIn("TryInstallTitleEntryFromText()", ui)
        self.assertIn("Archipelago_NativeTitleEntry", ui)
        self.assertIn("if (nativeStarterMenu != null) return false;", ui)
        self.assertIn("nativeStarterMenu = null;", ui)
        self.assertIn("observedMapAssets.Clear();", ui)
        self.assertIn('String.Equals(name, "SceneMenu"', ui)
        self.assertIn("if (titleSceneSeen && !introCreditShown", ui)
        self.assertIn("IsNativeTitleReady()", ui)
        self.assertNotIn("now - titleSceneReadyAt >= 4.0f", ui)
        self.assertIn("ShiftTitleOptionsBelow(playRect", ui)
        self.assertIn("MatchTitlePrimaryVisual(playRect, copy)", ui)
        self.assertIn("ProbeTitleScreenHierarchy()", ui)

    def test_login_uses_clean_text_layout_and_warm_game_palette(self):
        panel = (ROOT / "mod/NativeConnectionPanel.cs").read_text(encoding="utf-8")
        self.assertIn("TextAnchor.MiddleLeft", panel)
        self.assertIn("TextAnchor.MiddleCenter", panel)
        self.assertIn('new Color(.95f, .79f, .34f, 1f)', panel)
        self.assertIn("rect.SetParent(obj.transform, false);", panel)
        self.assertIn("nativeConnectPending", panel)
        self.assertIn("Already connecting; duplicate click ignored", panel)
        self.assertNotIn("UnityEngine.Object.Instantiate(item.gameObject)", panel)

    def test_missing_tmp_resolution_is_not_retried_for_every_button(self):
        native = INTEGRATION.read_text(encoding="utf-8")
        self.assertNotIn('"TMPro.TextMeshProUGUI"', native)
        self.assertIn("CachedNativeUiLabels(", native)
        self.assertIn('ScheduleNativeUiFilter("legacy title interaction")', native)
        self.assertIn("pendingFilterPasses = 3;", native)

    def test_native_mode_options_are_filtered_even_without_selectable(self):
        code = INTEGRATION.read_text(encoding="utf-8")
        self.assertIn("HideNativeNonSelectableChoices(selectedNativeMode)", code)
        self.assertIn("private GameObject FindNativeChoiceRoot(", code)
        self.assertIn("HasNativeInteractiveSurface", code)
        self.assertIn("CountDistinctNativeChoices(node) > 1", code)
        self.assertIn("HideTextBasedPlaySubmenuChoices();", code)
        self.assertIn("FindTextBasedPlaySubmenu()", code)
        self.assertIn("(hiddenNativeEntries.ContainsKey(target) && !target.activeSelf)", code)
        self.assertIn("HideNativeEntry(target);", code)
        self.assertIn("Hidden entire native choice:", code)

    def test_f7_never_falls_back_to_old_popup_only_due_to_missing_title_button(self):
        code = (ROOT / "mod/NativeConnectionPanel.cs").read_text(encoding="utf-8")
        self.assertIn("EnsureNativeConnectionTemplate()", code)
        self.assertIn("AP_NativeConnectionButtonTemplate", code)
        self.assertIn("Resources.GetBuiltinResource<Font>", code)

    def test_connection_command_is_acked_and_retried_in_memory(self):
        ui = UI.read_text(encoding="utf-8")
        plugin = PLUGIN.read_text(encoding="utf-8")
        bridge = (ROOT / "client/bridge.py").read_text(encoding="utf-8")
        self.assertIn("pendingBridgePassword", ui)
        self.assertIn("pendingBridgeRequestId", ui)
        self.assertIn("private void MaintainBridgeHandshake()", ui)
        self.assertIn("MaintainBridgeHandshake();", plugin)
        self.assertIn("bridge_command_ack.json", bridge)
        self.assertIn('"request_id": request_id', bridge)
        self.assertNotIn('save_json(self.directory / "connection.json"', bridge)

    def test_title_reinstalls_after_save_quit_scene_transition(self):
        code = INTEGRATION.read_text(encoding="utf-8")
        self.assertIn("nativeArchipelagoButton = null;", code)
        self.assertIn("mainMenuPlayTemplate = null;", code)
        self.assertIn("nativeStarterMenu = null;", code)
        self.assertIn("hiddenNativeEntries.Clear();", code)
        self.assertIn("lastNativeFilterKey = null;", code)
        self.assertIn("observedMapAssets.Clear();", code)
        self.assertIn("mainMenuProbeCount = 0;", code)
        self.assertIn("Rebuilding Archipelago title UI", code)
        self.assertIn("HideNativeConnectionPanel();", code)
        # Replay credits only at first boot; preserve the introCreditShown state.
        scene = code.split("private void OnArchipelagoSceneChanged(", 1)[1]
        scene = scene.split("private void UpdateNativeArchipelagoIntegration()", 1)[0]
        self.assertNotIn("introCreditShown = false;", scene)

    def test_native_hover_uses_real_play_template_and_actions_do_not_replay(self):
        integration = INTEGRATION.read_text(encoding="utf-8")
        panel = (ROOT / "mod/NativeConnectionPanel.cs").read_text(encoding="utf-8")
        self.assertIn("FindRealTitlePlayButton(label)", integration)
        self.assertIn("UnityEngine.Object.Instantiate(realPlay)", integration)
        self.assertIn("RemoveOriginalTitleClickTriggers(copy);", integration)
        self.assertIn('name == "PointerClick"', integration)
        self.assertIn("ConfigureFallbackPrimaryHover(copy);", integration)
        self.assertIn("ConfigureFallbackPrimaryHover(cloned);", panel)
        self.assertIn('hover=" + (hasNativeHover ?', panel)

    def test_unwanted_native_play_options_are_explicitly_removed(self):
        integration = INTEGRATION.read_text(encoding="utf-8")
        self.assertIn('case "éditeur de monde":', integration)
        self.assertIn('case "île de la renommée":', integration)
        self.assertIn("HideNativeNonSelectableChoices(selectedNativeMode)", integration)
        self.assertIn('case "nouvelle partie":', integration)
        self.assertIn('case "charger partie":', integration)
        self.assertIn('case "multijoueur":', integration)

    def test_gameplay_lua_menu_does_not_schedule_unbounded_ui_passes(self):
        hooks = NATIVE.read_text(encoding="utf-8")
        self.assertIn('if (simple == "luamenu")', hooks)
        self.assertIn('if (plugin.titleSceneSeen && plugin.bridgeConnected', hooks)
        self.assertIn('plugin.lastLuaTitleFilterAt > .65f', hooks)
        self.assertIn('plugin.nativeStarterMenu != null))', hooks)

    def test_ui_policies_are_prepared_on_connection_and_rendered_atomically(self):
        native = INTEGRATION.read_text(encoding="utf-8")
        hooks = NATIVE.read_text(encoding="utf-8")
        plugin = PLUGIN.read_text(encoding="utf-8")
        saves = (ROOT / "mod/SaveIsolation.cs").read_text(encoding="utf-8")
        pre = (ROOT / "mod/NativePreRenderHooks.cs").read_text(encoding="utf-8")
        self.assertIn("InstallNativePreRenderHooks();", plugin)
        self.assertIn("OnApAuthenticationChanged(bridgeConnected);", plugin)
        self.assertIn("ApplyPrecomputedPolicyToVisibleTitles();", saves)
        self.assertNotIn("ApplyPrecomputedPolicyToVisibleTitles();", plugin)
        self.assertIn("PropertySetter(textType, \"text\")", pre)
        self.assertIn("Canvas.willRenderCanvases +=", pre)
        self.assertIn("ApplyNativeCaptionBeforeRender(", pre)
        self.assertIn("IsWithinNativeLoadLibrary(text.transform)", pre)
        self.assertIn("FindNativeSaveItemFromCaption(text)", pre)
        self.assertIn("ReconcileKnownNativeSaveCards();", saves)
        self.assertIn("RebuildSaveVisibilityPolicy(\"connection state changed\")", saves)
        self.assertIn("if (nativeCaptionHookInstalled)", native)
        self.assertIn("if (!Instance.nativeCaptionHookInstalled)", hooks)
        self.assertNotIn('ScheduleNativeSaveFilter("native title UI refreshed")', native)
        self.assertIn("if (!nativeCaptionHookInstalled &&", native)
        self.assertIn("if (!nativeCaptionHookInstalled &&", saves)

    def test_save_tag_registry_accepts_empty_index_and_hot_reload(self):
        saves = (ROOT / "mod/SaveIsolation.cs").read_text(encoding="utf-8")
        self.assertIn("if (registry.saves == null)", saves)
        self.assertIn("Empty valid index", saves)
        self.assertIn("RefreshSaveRegistryWhenModified()", saves)
        self.assertIn("GetLastWriteTimeUtc(path)", saves)
        self.assertNotIn("File.Replace(temp, SaveTagsPath", saves)
        self.assertIn("authenticatedApSaveSession && !disconnectPending", saves)
        self.assertIn("lastPrimaryTagStamp", saves)

    def test_pre_render_never_deactivates_a_pooled_brix_card_during_build(self):
        pre = (ROOT / "mod/NativePreRenderHooks.cs").read_text(encoding="utf-8")
        self.assertIn("private void HidePreRenderCard(GameObject card)", pre)
        self.assertIn("private void ShowPreRenderCard(GameObject card)", pre)
        self.assertIn("CanvasGroup group = card.GetComponent<CanvasGroup>();", pre)
        self.assertIn("state.group.alpha = 0f;", pre)
        self.assertIn("state.group.blocksRaycasts = false;", pre)
        self.assertIn('SetUiProperty(state.layout, "ignoreLayout", true);', pre)
        self.assertIn("FindIndividualNativeChoice(text, isMap)", pre)
        self.assertIn('name.IndexOf("GamemodeButton"', pre)
        self.assertIn('name.IndexOf("Btn_DblTxt_RightIcon"', pre)
        self.assertIn('name.IndexOf("ThumbnailListItem"', pre)
        self.assertIn("ReconcileKnownNativeSaveCards()", pre)
        self.assertIn("ClearPreRenderCardPolicy()", INTEGRATION.read_text(encoding="utf-8"))
        self.assertNotIn("HideNativeEntry(card);", pre)
        self.assertNotIn("HideNativeEntry(choice);", pre)
        self.assertNotIn("FindNativeChoiceRoot(text)", pre)
        self.assertNotRegex(pre, r"\.\s*SetActive\s*\(\s*false\s*\)\s*;")

    def test_map_cards_are_resolved_from_verified_native_catalog(self):
        pre = (ROOT / "mod/NativePreRenderHooks.cs").read_text(encoding="utf-8")
        self.assertIn("TryResolveNativeMapCaption(caption, out mapId)", pre)
        self.assertIn("observedMapAssets.Keys", pre)
        self.assertIn("FindNativeMapCardFromCaption(caption)", pre)
        self.assertIn("IsWithinNativeNewGameLibrary(caption.transform)", pre)
        self.assertIn('name.IndexOf("NewGameStarterMenu"', pre)
        self.assertIn('Logger.LogInfo("[AP MAP UI] id="', pre)
        self.assertIn("GetComponents<Component>()", pre)
        self.assertNotIn("FindNativeChoiceRoot(text)", pre)

    def test_save_registry_can_restore_proven_ownership_from_backups(self):
        saves = (ROOT / "mod/SaveIsolation.cs").read_text(encoding="utf-8")
        self.assertIn("RecoverApSaveTagsFromBackups()", saves)
        self.assertIn('"save_tags.json.bak*"', saves)
        self.assertIn('"upgrade-backup-*"', saves)
        self.assertIn('".archipelago-save-tags.json.bak*"', saves)
        self.assertIn("conflicting.Add(folder)", saves)
        self.assertIn("recoveredTags > 0", saves)
        self.assertIn("[AP SAVE RECOVERY]", saves)

    def test_native_card_subtitles_cannot_unmask_forbidden_action(self):
        pre = (ROOT / "mod/NativePreRenderHooks.cs").read_text(encoding="utf-8")
        self.assertIn("if (IsAllowedPlaySubmenuEntry(caption))", pre)
        self.assertIn("ShowPreRenderCard(allowedAction)", pre)
        self.assertIn("(isModeCard && nativeMode != null)", pre)
        self.assertIn("(isMapCard && isMap)", pre)
        self.assertNotIn("(isPlayCard && forbiddenPlay == false)", pre)
        self.assertIn("if (cardRoot == null) return;", pre)

    def test_native_map_selection_binds_seed_mission_and_protects_checks(self):
        ui = UI.read_text(encoding="utf-8")
        native = NATIVE.read_text(encoding="utf-8")
        self.assertIn("else DrawArchipelagoIntroCredit();", ui)
        self.assertIn("if (apMenuOpen) DrawConnection();", ui)
        panel = (ROOT / "mod/NativeConnectionPanel.cs").read_text(encoding="utf-8")
        self.assertIn("UnityEngine.UI.InputField", panel)
        self.assertIn("NativeConnectAction", panel)
        self.assertIn("NativeDisconnectAction", panel)
        self.assertIn('SetUiProperty(passwordNativeInput, "text", "")', panel)
        self.assertIn("SelectMission(matching);", native)
        self.assertIn("startedMission = matching.id;", native)
        self.assertIn("String.Equals(MissionMapId(mission), mapId", native)
        self.assertIn("String.Equals(mission.mode, modeId", native)
        self.assertIn("return false;", native)
        self.assertIn("Blocked null native map starter", native)
        self.assertNotIn('Name.Contains("SpNewGameStarterMenu")', native)
        self.assertIn("ObserveNativeMapLabel(__0, __result as string)", native)
        self.assertIn("Instance.nativeNewGameVisible = false;", native)

    def test_intro_uses_bundled_official_logo_without_modifying_splash_assets(self):
        native = INTEGRATION.read_text(encoding="utf-8")
        self.assertIn("DrawArchipelagoIntroCredit()", native)
        self.assertIn("LoadApLogo();", native)
        self.assertIn("GUI.DrawTexture(new Rect((Screen.width - size)", native)
        self.assertIn("Texture2D.whiteTexture", native)
        self.assertIn("HasNativeOptionsSibling", native)
        self.assertIn("BeginOwnIntroCredit();", native)
        self.assertIn('Logger.LogInfo("[AP CREDITS] Title Play+Options detected', native)
        workflow = (ROOT / ".github/workflows/build.yml").read_text(encoding="utf-8")
        self.assertIn("archipelago-logo.png", workflow)

    def test_legacy_dialog_helpers_still_do_not_override_native_screen(self):
        native = NATIVE.read_text(encoding="utf-8")
        self.assertIn("seed filter retained", native)
        self.assertNotIn("Instance.RestoreNativeEntries();", native)
        self.assertIn("if (disconnectPending || String.IsNullOrEmpty(token)) return true;", native)
        ui = UI.read_text(encoding="utf-8")
        self.assertIn('bridgeConnected ? "Retour au jeu" : "Se connecter"', ui)
        self.assertIn("[AP UI EXCEPTION]", ui)


if __name__ == "__main__":
    unittest.main()
