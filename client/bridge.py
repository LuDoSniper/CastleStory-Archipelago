"""File bridge between Archipelago and the Castle Story BepInEx plugin."""
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

def token_for(seed, slot, player):
    return hashlib.sha256(f"{seed}/{slot}/{player}".encode()).hexdigest()[:24]

def normalize_url(address):
    if address.startswith(("ws://", "wss://")):
        return address
    return "ws://" + address

async def send(ws, *packets):
    await ws.send(json.dumps(list(packets)))

class Bridge:
    def __init__(self, directory, player, password):
        self.directory = directory
        self.player = player
        self.password = password
        self.seed = ""
        self.token = ""
        self.slot = None
        self.received = []
        self.seen_checks = set()
        self.goal_sent = False
        self.connected = False
        self.identifier = str(uuid4())

    async def on_message(self, ws, packet):
        command = packet.get("cmd")
        if command == "RoomInfo":
            self.seed = packet.get("seed_name", "")
            await send(ws, {"cmd":"Connect", "password":self.password, "game":GAME,
                 "name":self.player, "uuid":self.identifier,
                 "version":{"major":0,"minor":6,"build":7,"class":"Version"},
                 "items_handling":7,"tags":[],"slot_data":True})
        elif command == "Connected":
            self.slot = int(packet["slot"])
            self.token = token_for(self.seed, self.slot, self.player)
            self.connected = True
            self.received = []
            self.seen_checks = set()
            self.goal_sent = False
            slot_data = packet.get("slot_data") or {}
            save_json(self.directory/"session.json", {
                "token":self.token, "seed":self.seed, "slot":self.slot,
                "slot_name":self.player, "goal_wave":int(slot_data.get("goal_wave",10))})
            save_json(self.directory/"items.json", {"token":self.token,"items":[]})
            await send(ws, {"cmd":"Sync"})
        elif command == "ConnectionRefused":
            raise RuntimeError(f"Connection refused: {packet.get('errors')}")
        elif command == "ReceivedItems" and self.connected:
            index = int(packet.get("index",0))
            incoming = packet.get("items",[])
            if index > len(self.received):
                await send(ws, {"cmd":"Sync"})
                return
            self.received = self.received[:index] + incoming
            save_json(self.directory/"items.json", {"token":self.token, "items":[
                {"index":i,"item":int(v["item"]),"from_player":v.get("player",0)}
                for i,v in enumerate(self.received)]})
            LOG.info("Received %d item(s)", len(self.received))
        elif command == "PrintJSON":
            LOG.info("AP: %s", "".join(str(e.get("text","")) for e in packet.get("data",[])))

    async def poll_game(self, ws):
        while True:
            await asyncio.sleep(0.5)
            if not self.connected:
                continue
            message = load_json(self.directory/"checks.json")
            if not isinstance(message,dict) or message.get("token") != self.token:
                continue
            checks = message.get("location_ids",[])
            if not isinstance(checks,list):
                continue
            valid = {x for x in checks if type(x) is int and WAVE_BASE+1 <= x <= WAVE_BASE+10}
            pending = valid-self.seen_checks
            if pending:
                await send(ws,{"cmd":"LocationChecks","locations":sorted(pending)})
                self.seen_checks.update(pending)
                LOG.info("Checks sent: %s", sorted(pending))
            if message.get("goal") is True and not self.goal_sent and WAVE_BASE+10 in valid:
                await send(ws,{"cmd":"StatusUpdate","status":30})
                self.goal_sent = True

async def run_once(url, bridge):
    bridge.connected = False
    async with websockets.connect(url, ping_interval=20) as ws:
        async def consume():
            async for raw in ws:
                for packet in json.loads(raw):
                    await bridge.on_message(ws,packet)
        consumer = asyncio.create_task(consume())
        poller = asyncio.create_task(bridge.poll_game(ws))
        done,pending = await asyncio.wait([consumer,poller],return_when=asyncio.FIRST_COMPLETED)
        for task in pending: task.cancel()
        await asyncio.gather(*pending, return_exceptions=True)
        for task in done: task.result()

async def main(args):
    bridge = Bridge(Path(args.bridge_dir).resolve(),args.slot,args.password)
    delay = 1
    while True:
        try:
            await run_once(normalize_url(args.server),bridge)
            delay = 1
        except (OSError,websockets.exceptions.WebSocketException,RuntimeError,ValueError) as exc:
            LOG.error("Disconnected: %s",exc)
        await asyncio.sleep(delay)
        delay = min(delay*2,30)

if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--server",required=True)
    p.add_argument("--slot",required=True)
    p.add_argument("--password",default="")
    p.add_argument("--bridge-dir",required=True)
    logging.basicConfig(level=logging.INFO)
    asyncio.run(main(p.parse_args()))
