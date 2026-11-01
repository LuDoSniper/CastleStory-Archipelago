"""Archipelago websocket client for the Castle Story BepInEx file bridge."""
import argparse
import asyncio
import hashlib
import json
import logging
import os
from pathlib import Path
from uuid import uuid4

import websockets

LOG = logging.getLogger("castle_story_ap")
GAME = "Castle Story"
WAVE_BASE = 276810000
CONQUEST_BASE = 276812000
ITEM_BASE = 276811000
IDS = {
    "Invasion": set(range(WAVE_BASE + 1, WAVE_BASE + 11)),
    "Conquest": {CONQUEST_BASE + 1, CONQUEST_BASE + 2, CONQUEST_BASE + 3, CONQUEST_BASE + 100},
}
ITEM_NAMES = {
    ITEM_BASE + 1: "Archer Unlock", ITEM_BASE + 2: "Knight Unlock",
    ITEM_BASE + 3: "Catapult Unlock", ITEM_BASE + 4: "Bricktron Reinforcement",
    ITEM_BASE + 5: "Wood Supply", ITEM_BASE + 6: "Stone Supply",
}

def load_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None

def save_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".tmp")
    temp.write_text(json.dumps(data, indent=2), encoding="utf-8")
    os.replace(temp, path)

def token_for(seed, slot, player, team=0):
    return hashlib.sha256(f"{seed}/{team}/{slot}/{player}".encode()).hexdigest()[:24]

def normalize_url(address):
    if address.startswith(("ws://", "wss://")):
        return address
    return "ws://" + address

async def send(ws, *packets):
    await ws.send(json.dumps(list(packets)))

class Bridge:
    def __init__(self, directory, player, password):
        self.directory = Path(directory)
        self.player = player
        self.password = password
        self.seed = ""
        self.token = ""
        self.slot = None
        self.team = 0
        self.mode = ""
        self.received = []
        self.seen_checks = set()
        self.goal_sent = False
        self.connected = False
        self.identifier = str(uuid4())

    def session_data(self):
        return {
            "token": self.token, "seed": self.seed,
            "slot": self.slot, "team": self.team,
            "slot_name": self.player, "mode": self.mode,
            "goal_wave": 10, "connected": self.connected,
        }

    def persist_session(self):
        if self.token:
            save_json(self.directory / "session.json", self.session_data())

    def persist_items(self):
        save_json(self.directory / "items.json", {
            "token": self.token,
            "items": [
                {"index": i, "item": int(v["item"]), "from_player": v.get("player", 0)}
                for i, v in enumerate(self.received)
            ],
        })

    async def on_message(self, ws, packet):
        command = packet.get("cmd")
        if command == "RoomInfo":
            self.seed = packet.get("seed_name", "")
            await send(ws, {
                "cmd": "Connect", "password": self.password, "game": GAME,
                "name": self.player, "uuid": self.identifier,
                "version": {"major": 0, "minor": 6, "build": 7, "class": "Version"},
                "items_handling": 7, "tags": [], "slot_data": True,
            })
        elif command == "Connected":
            slot_data = packet.get("slot_data") or {}
            mode = slot_data.get("game_mode", "Invasion")
            if mode not in IDS:
                raise RuntimeError(f"Unsupported Castle Story mode in slot data: {mode!r}")
            self.mode = mode
            self.slot = int(packet["slot"])
            self.team = int(packet.get("team", 0))
            self.token = token_for(self.seed, self.slot, self.player, self.team)
            self.connected = True
            self.received = []
            self.seen_checks.clear()
            self.goal_sent = False
            self.persist_session()
            self.persist_items()
            LOG.info("Connected: slot=%s mode=%s seed=%s", self.player, mode, self.seed)
            await send(ws, {"cmd": "Sync"})
        elif command == "ConnectionRefused":
            raise RuntimeError(f"Connection refused: {packet.get('errors')}")
        elif command == "ReceivedItems" and self.connected:
            index = int(packet.get("index", 0))
            incoming = packet.get("items", [])
            if index > len(self.received):
                await send(ws, {"cmd": "Sync"})
                return
            # Index 0 is the full inventory; later indices are authoritative suffixes.
            self.received = self.received[:index] + incoming
            self.persist_items()
            LOG.info("Received %d item(s); inventory total=%d",
                     len(incoming), len(self.received))
        elif command == "PrintJSON":
            LOG.info("AP: %s", "".join(str(e.get("text", "")) for e in packet.get("data", [])))
        elif command == "RoomUpdate":
            LOG.info("Room state updated")

    async def poll_once(self, ws):
        if not self.connected:
            return
        msg = load_json(self.directory / "checks.json")
        if not isinstance(msg, dict) or msg.get("token") != self.token:
            return
        if msg.get("mode") not in (None, self.mode):
            LOG.warning("Ignored checks file with different game mode")
            return
        checks = msg.get("location_ids", [])
        if not isinstance(checks, list):
            return
        valid = {
            x for x in checks if type(x) is int and x in IDS[self.mode]
        }
        unknown = set(x for x in checks if type(x) is int) - valid
        if unknown:
            LOG.warning("Ignored %d invalid/mode-incompatible location IDs", len(unknown))
        pending = valid - self.seen_checks
        if pending:
            await send(ws, {"cmd": "LocationChecks", "locations": sorted(pending)})
            self.seen_checks.update(pending)
            LOG.info("Sent %s location checks: %s", self.mode, sorted(pending))
        goal_ready = (
            WAVE_BASE + 10 in valid if self.mode == "Invasion"
            else CONQUEST_BASE + 100 in valid
        )
        if msg.get("goal") is True and goal_ready and not self.goal_sent:
            await send(ws, {"cmd": "StatusUpdate", "status": 30})
            self.goal_sent = True
            LOG.info("Sent Archipelago GOAL for %s", self.mode)

    async def poll_game(self, ws):
        while True:
            await asyncio.sleep(0.5)
            await self.poll_once(ws)

async def run_once(url, bridge):
    bridge.connected = False
    bridge.persist_session()
    try:
        async with websockets.connect(url, ping_interval=20) as ws:
            async def consume():
                async for raw in ws:
                    for packet in json.loads(raw):
                        await bridge.on_message(ws, packet)
            consumer = asyncio.create_task(consume())
            poller = asyncio.create_task(bridge.poll_game(ws))
            done, pending = await asyncio.wait(
                [consumer, poller], return_when=asyncio.FIRST_COMPLETED)
            for task in pending:
                task.cancel()
            await asyncio.gather(*pending, return_exceptions=True)
            for task in done:
                task.result()
    finally:
        bridge.connected = False
        bridge.persist_session()

async def main(args):
    bridge = Bridge(Path(args.bridge_dir).resolve(), args.slot, args.password)
    delay = 1
    while True:
        try:
            await run_once(normalize_url(args.server), bridge)
            delay = 1
        except (OSError, websockets.exceptions.WebSocketException,
                RuntimeError, ValueError, KeyError) as exc:
            LOG.error("Disconnected: %s", exc)
        await asyncio.sleep(delay)
        delay = min(delay * 2, 30)

if __name__ == "__main__":
    p = argparse.ArgumentParser(description="Castle Story Archipelago bridge")
    p.add_argument("--server", required=True, help="AP server address:port, ws:// or wss://")
    p.add_argument("--slot", required=True, help="Slot name from your Archipelago YAML")
    p.add_argument("--password", default="")
    p.add_argument("--bridge-dir", required=True,
                   help="Castle Story/BepInEx/config/castle_story_archipelago")
    logging.basicConfig(level=logging.INFO)
    asyncio.run(main(p.parse_args()))
