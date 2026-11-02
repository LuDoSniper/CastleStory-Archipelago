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
    "Conquest": set(range(CONQUEST_BASE + 1, CONQUEST_BASE + 31)) | {CONQUEST_BASE + 100},
}
IDS["Hybrid"] = IDS["Invasion"] | IDS["Conquest"]
ITEM_NAMES = {
    ITEM_BASE + 1: "Archer Unlock", ITEM_BASE + 2: "Knight Unlock",
    ITEM_BASE + 3: "Catapult Unlock", ITEM_BASE + 4: "Bricktron Reinforcement",
    ITEM_BASE + 5: "Wood Supply", ITEM_BASE + 6: "Stone Supply",
    ITEM_BASE + 7: "Alchemist Unlock", ITEM_BASE + 8: "Equipped Worker",
    ITEM_BASE + 9: "Equipped Archer", ITEM_BASE + 10: "Equipped Knight",
    ITEM_BASE + 11: "Equipped Alchemist", ITEM_BASE + 12: "Wisp Supply",
}

def detect_installed_maps(directory):
    """Read local map names only; the seed supplies verified shard counts."""
    location = Path(directory).resolve()
    # Only probe inside the canonical Castle Story/BepInEx/config location.
    # Tests and custom bridge directories must not be mistaken for game installs.
    if (location.name != "castle_story_archipelago" or
            location.parent.name != "config" or
            location.parent.parent.name != "BepInEx"):
        return None
    root = location.parents[2]
    found = set()
    sources = 0
    for suffix in ("Info/Maps", "Info/UGC/Maps"):
        parent = root / suffix
        if parent.is_dir():
            sources += 1
            found.update(child.name for child in parent.iterdir() if child.is_dir())
    return found if sources else None

def reconcile_local_maps(directory, campaign):
    """Report unavailable seed-selected maps without inventing replacements."""
    installed = detect_installed_maps(directory)
    if installed is None:
        return campaign
    selected = [m.get("map") for m in campaign.get("missions", [])
                if isinstance(m, dict)]
    missing = sorted(x for x in selected if isinstance(x, str) and x not in installed)
    if missing:
        updated = dict(campaign)
        updated["ready"] = False
        updated["warning"] = ("Missing maps on this PC: " + ", ".join(missing))
        LOG.warning("Campaign maps missing locally: %s", ", ".join(missing))
        return updated
    return campaign

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
        self.campaign = {}
        self.goal_wave = 10
        self.shard_goal = 3
        self.allowed_ids = set()
        self.connection_request_path = self.directory / "connection.json"
        self.observed_request = None
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
            "goal_wave": self.goal_wave, "shard_goal": self.shard_goal,
            "allowed_location_ids": sorted(self.allowed_ids),
            "campaign": self.campaign,
            "connected": self.connected,
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
            self.campaign = reconcile_local_maps(self.directory, slot_data.get("campaign") or {})
            self.goal_wave = int(slot_data.get("goal_wave", 10))
            self.shard_goal = int(slot_data.get("conquest_capture_goal", 3))
            ids = slot_data.get("location_ids")
            self.allowed_ids = (set(x for x in ids if type(x) is int) & IDS[mode]
                                if isinstance(ids, list) else IDS[mode].copy())
            self.slot = int(packet["slot"])
            self.team = int(packet.get("team", 0))
            self.token = token_for(self.seed, self.slot, self.player, self.team)
            self.connected = True
            write_status(self.directory, "connected", "Connected to Archipelago as " + self.player)
            self.received = []
            self.seen_checks.clear()
            self.goal_sent = False
            self.persist_session()
            self.persist_items()
            LOG.info("Connected: slot=%s mode=%s seed=%s campaign_ready=%s",
                     self.player, mode, self.seed, self.campaign.get("ready", False))
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
        valid = {x for x in checks if type(x) is int and x in self.allowed_ids}
        unknown = set(x for x in checks if type(x) is int) - valid
        if unknown:
            LOG.warning("Ignored %d invalid/mode-incompatible location IDs", len(unknown))
        pending = valid - self.seen_checks
        if pending:
            await send(ws, {"cmd": "LocationChecks", "locations": sorted(pending)})
            self.seen_checks.update(pending)
            LOG.info("Sent %s location checks: %s", self.mode, sorted(pending))
        invasion_done = WAVE_BASE + self.goal_wave in valid
        conquest_done = (CONQUEST_BASE + 100 in valid and
                         CONQUEST_BASE + self.shard_goal in valid)
        goal_ready = (
            invasion_done if self.mode == "Invasion"
            else conquest_done if self.mode == "Conquest"
            else invasion_done and conquest_done
        )
        if msg.get("goal") is True and goal_ready and not self.goal_sent:
            await send(ws, {"cmd": "StatusUpdate", "status": 30})
            self.goal_sent = True
            LOG.info("Sent Archipelago GOAL for %s", self.mode)

    async def poll_game(self, ws):
        while True:
            await asyncio.sleep(0.5)
            if (getattr(self, "request_snapshot", None) is not None and
                    read_connection_request(self.directory) != self.request_snapshot):
                raise ConnectionChanged()
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

class ConnectionChanged(Exception):
    """User edited in-game AP connection settings; reconnect cleanly."""
    pass

def read_connection_request(directory):
    """Main-menu frontend writes connection.json; password stays local."""
    data = load_json(Path(directory) / "connection.json")
    if not isinstance(data, dict) or data.get("enabled") is not True:
        return None
    address = str(data.get("server", "")).strip()
    slot = str(data.get("slot", "")).strip()
    password = str(data.get("password", ""))
    if not address or not slot:
        return None
    if not address.startswith(("ws://", "wss://")):
        address = normalize_url(address)
    return (address, slot, password)

def write_status(directory, state, message):
    save_json(Path(directory) / "bridge_status.json", {
        "state": state, "message": message
    })

async def main(args):
    directory = Path(args.bridge_dir).resolve()
    bridge = None
    active = None
    delay = 1
    static = (normalize_url(args.server), args.slot, args.password) if args.server and args.slot else None
    while True:
        requested = static if static is not None else read_connection_request(directory)
        if requested is None:
            if bridge is not None:
                bridge.connected = False
                bridge.persist_session()
                bridge = None
            active = None
            write_status(directory, "idle", "Open the AP menu in Castle Story to connect.")
            await asyncio.sleep(1)
            continue
        if requested != active:
            if bridge is not None:
                bridge.connected = False
                bridge.persist_session()
            active = requested
            bridge = Bridge(directory, requested[1], requested[2])
            bridge.request_snapshot = requested if static is None else None
            delay = 1
        try:
            write_status(directory, "connecting", "Connecting to Archipelago...")
            await run_once(requested[0], bridge)
            delay = 1
        except ConnectionChanged:
            write_status(directory, "reconnecting", "Settings changed in game.")
            delay = 1
        except (OSError, websockets.exceptions.WebSocketException,
                RuntimeError, ValueError, KeyError) as exc:
            message = str(exc).replace(requested[2], "[redacted]") if requested[2] else str(exc)
            LOG.error("Disconnected: %s", message)
            write_status(directory, "disconnected", message[:300])
        # Poll the menu request even while a server cannot be reached.
        await asyncio.sleep(min(delay, 2) if static is None else delay)
        delay = min(delay * 2, 30)

if __name__ == "__main__":
    p = argparse.ArgumentParser(description="Castle Story Archipelago bridge")
    p.add_argument("--server", default="", help="Optional initial AP server. Omit to use the in-game AP login.")
    p.add_argument("--slot", default="", help="Optional AP slot name, paired with --server")
    p.add_argument("--password", default="")
    p.add_argument("--bridge-dir", required=True,
                   help="Castle Story/BepInEx/config/castle_story_archipelago")
    logging.basicConfig(level=logging.INFO)
    asyncio.run(main(p.parse_args()))
