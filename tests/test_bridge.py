import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("bridge", ROOT/"client/bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)

class FakeWS:
    def __init__(self):
        self.messages = []
    async def send(self, raw):
        self.messages.extend(json.loads(raw))

class BridgeTests(unittest.IsolatedAsyncioTestCase):
    async def test_handshake_and_inventory(self):
        with tempfile.TemporaryDirectory() as td:
            ws = FakeWS()
            b = bridge.Bridge(Path(td), "Tester", "")
            await b.on_message(ws, {"cmd":"RoomInfo","seed_name":"test"})
            self.assertEqual(ws.messages[0]["cmd"],"Connect")
            await b.on_message(ws, {"cmd":"Connected","slot":1})
            self.assertEqual(ws.messages[-1]["cmd"],"Sync")
            await b.on_message(ws, {"cmd":"ReceivedItems","index":0,
                "items":[{"item":276811001,"player":2}]})
            data = bridge.load_json(Path(td)/"items.json")
            self.assertEqual(data["items"][0]["item"],276811001)
            self.assertEqual(bridge.load_json(Path(td)/"session.json")["seed"],"test")
    async def test_missing_item_range_requests_sync(self):
        with tempfile.TemporaryDirectory() as td:
            ws = FakeWS()
            b = bridge.Bridge(Path(td),"Tester","")
            await b.on_message(ws,{"cmd":"RoomInfo","seed_name":"test"})
            await b.on_message(ws,{"cmd":"Connected","slot":1})
            await b.on_message(ws,{"cmd":"ReceivedItems","index":5,"items":[]})
            self.assertEqual(ws.messages[-1]["cmd"],"Sync")
    def test_session_token(self):
        self.assertNotEqual(bridge.token_for("a",1,"X"),bridge.token_for("a",2,"X"))
        self.assertEqual(bridge.normalize_url("localhost:38281"),"ws://localhost:38281")
if __name__=="__main__":
    unittest.main()
