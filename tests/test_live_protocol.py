import asyncio
import json
from pathlib import Path
import tempfile
import unittest

import websockets
from test_bridge import bridge

class LiveProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_real_websocket_handshake_check_and_received_item(self):
        with tempfile.TemporaryDirectory() as td:
            directory = Path(td)
            client = bridge.Bridge(directory, "Tester", "")
            sequence = []

            async def server_handler(ws):
                await ws.send(json.dumps([{"cmd": "RoomInfo", "seed_name": "integration-seed"}]))
                connect = json.loads(await ws.recv())[0]
                self.assertEqual(connect["cmd"], "Connect")
                self.assertEqual(connect["game"], "Castle Story")
                await ws.send(json.dumps([{"cmd": "Connected", "slot": 4, "team": 0,
                    "slot_data": {"game_mode": "Conquest"}}]))
                sync = json.loads(await ws.recv())[0]
                self.assertEqual(sync["cmd"], "Sync")
                await ws.send(json.dumps([{"cmd": "ReceivedItems", "index": 0,
                    "items": [{"item": 276811001, "player": 2}]}]))
                for _ in range(25):
                    if client.connected:
                        break
                    await asyncio.sleep(0.02)
                bridge.save_json(directory / "checks.json", {
                    "token": client.token, "mode": "Conquest",
                    "location_ids": [276812001, 276812003, 276812100], "goal": True,
                })
                while len(sequence) < 2:
                    for packet in json.loads(await asyncio.wait_for(ws.recv(), timeout=3)):
                        sequence.append(packet)

            async with websockets.serve(server_handler, "127.0.0.1", 0) as server:
                port = server.sockets[0].getsockname()[1]
                await asyncio.wait_for(
                    bridge.run_once(f"ws://127.0.0.1:{port}", client), timeout=5
                )
            self.assertEqual([p["cmd"] for p in sequence],
                             ["LocationChecks", "StatusUpdate"])
            self.assertEqual(sequence[0]["locations"], [276812001, 276812003, 276812100])
            self.assertEqual(sequence[1]["status"], 30)
            self.assertFalse(bridge.load_json(directory / "session.json")["connected"])
            self.assertEqual(bridge.load_json(directory / "items.json")["items"][0]["item"],
                             276811001)

if __name__ == "__main__":
    unittest.main()
