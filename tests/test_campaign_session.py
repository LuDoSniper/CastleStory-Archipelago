import asyncio
from pathlib import Path
import tempfile
import unittest
from test_bridge import FakeWS, bridge


class CampaignManifestAndDisconnectTests(unittest.IsolatedAsyncioTestCase):
    async def create_slot(self, folder, slot_data):
        b = bridge.Bridge(folder, "Tester", "")
        ws = FakeWS()
        await b.on_message(ws, {"cmd": "RoomInfo", "seed_name": "sample-seed"})
        await b.on_message(ws, {
            "cmd": "Connected", "slot": 1, "team": 0,
            "slot_data": slot_data
        })
        return b, ws, bridge.load_json(folder / "session.json")

    async def test_legacy_seed_connects_but_reports_missing_campaign(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            b, ws, data = await self.create_slot(
                folder, {"game_mode": "Hybrid"}
            )
            self.assertTrue(data["connected"])
            self.assertEqual(data["mode"], "Hybrid")
            self.assertEqual(data["protocol_version"], 0)
            self.assertEqual(data["campaign_status"], "missing")
            self.assertIn("NOUVELLE seed", data["campaign_message"])
            self.assertEqual(data["campaign"], {})
            self.assertEqual(data["bridge_client_version"], "0.6.7")
            self.assertGreater(data["updated_at"], 0)
            self.assertTrue(data["slot_data_provided"])
            self.assertFalse(data["manifest_provided"])
            self.assertIn("game_mode", data["slot_data_fields"])
            self.assertEqual(ws.messages[-1]["cmd"], "Sync")

    async def test_valid_seed_manifest_exposed_to_unity(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            seed = {"ready": True, "mode": "Hybrid", "missions": [
                {"id": "invasion_1", "mode": "Invasion",
                 "map_id": "official:map-key", "map": "Localized Display Name",
                 "wave_goal": 5}
            ]}
            _, _, data = await self.create_slot(folder, {
                "protocol_version": 5, "game_mode": "Invasion",
                "campaign": seed
            })
            self.assertEqual(data["protocol_version"], 5)
            self.assertEqual(data["campaign_status"], "ready")
            self.assertEqual(data["campaign"]["missions"], seed["missions"])
            self.assertEqual(data["campaign_message"], "")
            self.assertTrue(data["manifest_provided"])
            self.assertIn("campaign", data["slot_data_fields"])

    async def test_unready_seed_manifest_includes_real_reason(self):
        with tempfile.TemporaryDirectory() as tmp:
            _, _, data = await self.create_slot(Path(tmp), {
                "protocol_version": 4, "game_mode": "Conquest",
                "campaign": {
                    "ready": False, "warning": "Missing verified shards",
                    "mode": "Conquest", "missions": []
                }
            })
            self.assertEqual(data["campaign_status"], "unready")
            self.assertEqual(data["campaign_message"], "Missing verified shards")

    async def test_invalid_campaign_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            _, _, data = await self.create_slot(Path(tmp), {
                "protocol_version": 4, "game_mode": "Invasion",
                "campaign": {"ready": True, "missions": "not an array"}
            })
            self.assertEqual(data["campaign_status"], "invalid")

    async def test_absent_slot_data_records_absence_without_guessing_manifest(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            b = bridge.Bridge(folder, "Tester", "")
            ws = FakeWS()
            await b.on_message(ws, {"cmd": "RoomInfo", "seed_name": "old"})
            await b.on_message(ws, {"cmd": "Connected", "slot": 1, "team": 0})
            data = bridge.load_json(folder / "session.json")
            self.assertTrue(data["connected"])
            self.assertFalse(data["slot_data_provided"])
            self.assertFalse(data["manifest_provided"])
            self.assertEqual(data["campaign_status"], "missing")
            self.assertEqual(data["slot_data_fields"], [])
            self.assertEqual(data["bridge_client_version"], "0.6.7")

    def test_ingame_disconnect_overrides_manual_cli_server(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            fallback = ("wss://old.example.invalid", "Tester", "")
            self.assertEqual(
                bridge.effective_connection_request(folder, fallback), fallback
            )
            bridge.save_json(folder / "connection.json", {
                "enabled": False, "server": "", "slot": "", "password": ""
            })
            self.assertIsNone(
                bridge.effective_connection_request(folder, fallback)
            )
            bridge.save_json(folder / "connection.json", {
                "enabled": True, "server": "wss://new.example.invalid",
                "slot": "AnotherTester", "password": ""
            })
            self.assertEqual(
                bridge.effective_connection_request(folder, fallback),
                ("wss://new.example.invalid", "AnotherTester", "")
            )

    async def test_active_poll_notices_ingame_disconnect(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            b = bridge.Bridge(folder, "Tester", "")
            b.connected = True
            b.mode = "Invasion"
            b.token = "test-token"
            fallback = ("wss://old.example.invalid", "Tester", "")
            b.request_snapshot = fallback
            b.fallback_request = fallback
            ws = FakeWS()
            bridge.save_json(folder / "connection.json", {
                "enabled": False, "server": "", "slot": "", "password": ""
            })
            with self.assertRaises(bridge.ConnectionChanged):
                await asyncio.wait_for(b.poll_game(ws), timeout=2)


if __name__ == "__main__":
    unittest.main()
