import json
from pathlib import Path
import tempfile
import unittest
from test_bridge import FakeWS, bridge

class HybridProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_hybrid_goal_requires_both_modes(self):
        with tempfile.TemporaryDirectory() as td:
            b = bridge.Bridge(Path(td), "HybridPlayer", "")
            ws = FakeWS()
            await b.on_message(ws, {"cmd": "RoomInfo", "seed_name": "seed"})
            await b.on_message(ws, {"cmd": "Connected", "slot": 2,
                "slot_data": {"game_mode": "Hybrid", "goal_wave": 4,
                              "conquest_capture_goal": 5,
                              "location_ids": [276810001, 276810004,
                                               276812001, 276812005, 276812100],
                              "campaign": {"ready": True, "missions": [
                                  {"id": "invasion_1", "mode": "Invasion", "map": "Pioneer Pass"},
                                  {"id": "conquest_1", "mode": "Conquest", "map": "TestMap"}]}}})
            session = bridge.load_json(Path(td) / "session.json")
            self.assertEqual(session["mode"], "Hybrid")
            self.assertEqual(len(session["campaign"]["missions"]), 2)
            bridge.save_json(Path(td) / "checks.json", {
                "token": b.token, "mode": "Hybrid",
                "location_ids": [276810004, 276812100, 276812031], "goal": True
            })
            await b.poll_once(ws)
            self.assertFalse(any(p["cmd"] == "StatusUpdate" for p in ws.messages))
            bridge.save_json(Path(td) / "checks.json", {
                "token": b.token, "mode": "Hybrid",
                "location_ids": [276810004, 276812005, 276812100], "goal": True
            })
            await b.poll_once(ws)
            self.assertEqual([p["cmd"] for p in ws.messages].count("StatusUpdate"), 1)
            sent = [p for p in ws.messages if p["cmd"] == "LocationChecks"]
            self.assertEqual(sent[-1]["locations"], [276812005])

    def test_menu_credentials_not_required_on_cli(self):
        with tempfile.TemporaryDirectory() as td:
            self.assertIsNone(bridge.read_connection_request(td))
            bridge.save_json(Path(td) / "connection.json", {
                "enabled": True, "server": "wss://example.invalid/path",
                "slot": "SomeSlot", "password": "abc"
            })
            self.assertEqual(bridge.read_connection_request(td),
                             ("wss://example.invalid/path", "SomeSlot", "abc"))

if __name__ == "__main__":
    unittest.main()
