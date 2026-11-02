import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("bridge", ROOT / "client/bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)

class FakeWS:
    def __init__(self):
        self.messages = []
    async def send(self, raw):
        self.messages.extend(json.loads(raw))

class BridgeTests(unittest.IsolatedAsyncioTestCase):
    async def connect(self, td, mode="Invasion"):
        ws = FakeWS()
        b = bridge.Bridge(Path(td), "Tester", "")
        await b.on_message(ws, {"cmd": "RoomInfo", "seed_name": "test"})
        self.assertEqual(ws.messages[0]["cmd"], "Connect")
        await b.on_message(ws, {"cmd": "Connected", "slot": 1, "team": 0,
                                "slot_data": {"game_mode": mode}})
        self.assertEqual(ws.messages[-1]["cmd"], "Sync")
        return ws, b

    async def test_handshake_inventory_and_disconnect(self):
        with tempfile.TemporaryDirectory() as td:
            ws, b = await self.connect(td)
            await b.on_message(ws, {"cmd": "ReceivedItems", "index": 0,
                           "items": [{"item": 276811001, "player": 2}]})
            self.assertEqual(bridge.load_json(Path(td)/"items.json")["items"][0]["item"], 276811001)
            self.assertTrue(bridge.load_json(Path(td)/"session.json")["connected"])
            b.connected = False
            b.persist_session()
            self.assertFalse(bridge.load_json(Path(td)/"session.json")["connected"])

    async def test_resync_on_gap(self):
        with tempfile.TemporaryDirectory() as td:
            ws, b = await self.connect(td)
            await b.on_message(ws, {"cmd": "ReceivedItems", "index": 5, "items": []})
            self.assertEqual(ws.messages[-1]["cmd"], "Sync")

    async def test_invasion_check_goal_and_reconnect(self):
        with tempfile.TemporaryDirectory() as td:
            ws, b = await self.connect(td)
            bridge.save_json(Path(td)/"checks.json", {
                "token": b.token, "mode": "Invasion",
                "location_ids": [276810001, 276810010, 276812001], "goal": True,
            })
            await b.poll_once(ws)
            checks = [x for x in ws.messages if x["cmd"] == "LocationChecks"]
            self.assertEqual(checks[-1]["locations"], [276810001, 276810010])
            self.assertEqual([x["cmd"] for x in ws.messages].count("StatusUpdate"), 1)
            await b.poll_once(ws)
            self.assertEqual([x["cmd"] for x in ws.messages].count("LocationChecks"), 1)

    async def test_conquest_checks_and_goal(self):
        with tempfile.TemporaryDirectory() as td:
            ws, b = await self.connect(td, "Conquest")
            bridge.save_json(Path(td)/"checks.json", {
                "token": b.token, "mode": "Conquest",
                "location_ids": [276812001, 276812003, 276812100, 276810001], "goal": True,
            })
            await b.poll_once(ws)
            checks = [x for x in ws.messages if x["cmd"] == "LocationChecks"]
            self.assertEqual(checks[-1]["locations"], [276812001, 276812003, 276812100])
            self.assertEqual([x["cmd"] for x in ws.messages].count("StatusUpdate"), 1)

    async def test_reject_stale_or_wrong_mode(self):
        with tempfile.TemporaryDirectory() as td:
            ws, b = await self.connect(td, "Conquest")
            bridge.save_json(Path(td)/"checks.json", {
                "token": "older-seed", "mode": "Conquest",
                "location_ids": [276812001], "goal": False,
            })
            await b.poll_once(ws)
            self.assertFalse(any(x["cmd"] == "LocationChecks" for x in ws.messages))
            bridge.save_json(Path(td)/"checks.json", {
                "token": b.token, "mode": "Invasion",
                "location_ids": [276812001], "goal": False,
            })
            await b.poll_once(ws)
            self.assertFalse(any(x["cmd"] == "LocationChecks" for x in ws.messages))

    async def test_duplicate_and_suffix_inventory(self):
        with tempfile.TemporaryDirectory() as td:
            ws,b=await self.connect(td)
            await b.on_message(ws, {"cmd":"ReceivedItems","index":0,
                "items":[{"item":276811001,"player":1}]})
            await b.on_message(ws, {"cmd":"ReceivedItems","index":1,
                "items":[{"item":276811002,"player":2}]})
            self.assertEqual(len(b.received),2)
            await b.on_message(ws, {"cmd":"ReceivedItems","index":0,
                "items":[{"item":276811001,"player":1},{"item":276811002,"player":2}]})
            self.assertEqual(len(b.received),2)

    def test_token_stability_and_url(self):
        self.assertNotEqual(bridge.token_for("a",1,"X"), bridge.token_for("a",1,"X",2))
        self.assertEqual(bridge.normalize_url("localhost:38281"), "ws://localhost:38281")

if __name__ == "__main__":
    unittest.main()
