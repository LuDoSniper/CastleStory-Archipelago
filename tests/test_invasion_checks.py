"""Regression tests for delivering checks that were queued during reconnects."""
from pathlib import Path
import tempfile
import unittest

from test_bridge import bridge, FakeWS

WAVE_BASE = bridge.WAVE_BASE


class InvasionCheckDeliveryTests(unittest.IsolatedAsyncioTestCase):
    async def make_bridge(self, folder):
        b = bridge.Bridge(folder, "Tester", "")
        ws = FakeWS()
        await b.on_message(ws, {"cmd": "RoomInfo", "seed_name": "seed-zuma"})
        await b.on_message(ws, {
            "cmd": "Connected", "slot": 1, "team": 0,
            "slot_data": {
                "game_mode": "Invasion", "goal_wave": 5,
                "protocol_version": 6,
                "campaign": {
                    "ready": True, "mode": "Invasion", "missions": [
                        {"id": "invasion_1", "mode": "Invasion",
                         "map_id": "official:Zuma", "map": "official:Zuma",
                         "wave_goal": 5}
                    ]
                }
            }
        })
        return b, ws

    async def test_five_checks_queue_offline_then_replay_on_reconnect(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            b, ws = await self.make_bridge(folder)
            self.assertEqual(b.campaign_status, "ready")
            b.connected = False
            b.persist_session()
            # The Unity mod writes this during the outage; no socket required.
            bridge.save_json(folder / "checks.json", {
                "token": b.token, "mode": "Invasion",
                "location_ids": [WAVE_BASE + n for n in range(1, 6)],
                "goal": True, "captured_keys": [], "completed_missions": []
            })
            self.assertFalse(bridge.load_json(folder / "session.json")["connected"])
            # Nothing is lost on reconnect.
            b.connected = True
            await b.poll_once(ws)
            sent = [x for x in ws.messages if x["cmd"] == "LocationChecks"]
            self.assertEqual(len(sent), 1)
            self.assertEqual(sent[0]["locations"],
                             [WAVE_BASE + n for n in range(1, 6)])
            self.assertEqual(
                [x for x in ws.messages if x["cmd"] == "StatusUpdate"],
                [{"cmd": "StatusUpdate", "status": 30}]
            )
            await b.poll_once(ws)
            self.assertEqual(len([x for x in ws.messages
                                  if x["cmd"] == "LocationChecks"]), 1)

    async def test_old_seed_checks_never_replay_into_new_seed(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            b, ws = await self.make_bridge(folder)
            bridge.save_json(folder / "checks.json", {
                "token": "old-seed-token", "mode": "Invasion",
                "location_ids": [WAVE_BASE + 1], "goal": False,
            })
            await b.poll_once(ws)
            self.assertFalse([x for x in ws.messages
                              if x["cmd"] == "LocationChecks"])

    def test_mod_automatically_enables_checks_only_for_launched_seed(self):
        root = Path(__file__).resolve().parents[1]
        mod = (root / "mod/CastleStoryArchipelagoPlugin.cs").read_text(encoding="utf-8")
        native = (root / "mod/NativeMenuHooks.cs").read_text(encoding="utf-8")
        self.assertIn("private bool IsActiveInvasionMission()", mod)
        self.assertIn('String.Equals(startedMission, chosenMission', mod)
        self.assertIn('startedMission = selected.id;', native)
        self.assertIn("[AP MISSION START]", native)
        self.assertIn("if (!activeSeedMission && (!waveChecks.Value || !bridgeConnected))", mod)
        self.assertIn("(!bridgeConnected && !IsActiveInvasionMission())", mod)
        self.assertIn("File.Replace(temp, path, null)", mod)
        self.assertIn("if (checksDirty && (bridgeConnected || IsActiveInvasionMission()))", mod)
        self.assertIn("startedMission = \"\";", mod)


if __name__ == "__main__":
    unittest.main()
