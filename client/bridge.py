"""Archipelago websocket client for the Castle Story BepInEx file bridge."""
import argparse
import base64
import asyncio
import copy
import hashlib
import json
import logging
import os
import time
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
AUTO_INVASION_MAP_ID = "auto:invasion"
BRIDGE_CLIENT_VERSION = "0.6.7"
ITEM_NAMES = {
    ITEM_BASE + 1: "Archer Unlock", ITEM_BASE + 2: "Knight Unlock",
    ITEM_BASE + 3: "Catapult Unlock", ITEM_BASE + 4: "Bricktron Reinforcement",
    ITEM_BASE + 5: "Wood Supply", ITEM_BASE + 6: "Stone Supply",
    ITEM_BASE + 7: "Alchemist Unlock", ITEM_BASE + 8: "Equipped Worker",
    ITEM_BASE + 9: "Equipped Archer", ITEM_BASE + 10: "Equipped Knight",
    ITEM_BASE + 11: "Equipped Alchemist", ITEM_BASE + 12: "Wisp Supply",
}

def canonical_map_id(name):
    """Canonical technical ID, never a translated name or an English label."""
    if not isinstance(name, str):
        return None
    prefix, sep, folder = name.partition(":")
    if (sep != ":" or prefix not in ("official", "workshop") or
            not folder or "/" in folder or "\\" in folder or
            folder != folder.strip() or folder in (".", "..")):
        return None
    return (prefix + ":" + folder).casefold()


def detect_installed_maps(directory):
    """Advisory local map-folder scan, not an authoritative installed-map list.

    Castle Story may package built-in maps in assets; localized titles and
    physical folder names differ. A missing folder is NOT proof that the game
    does not own the map. The Unity Asset_Map loader is authoritative.
    """
    location = Path(directory).resolve()
    if (location.name != "castle_story_archipelago" or
            location.parent.name != "config" or
            location.parent.parent.name != "BepInEx"):
        return None
    root = location.parents[2]
    found = set()
    sources = 0
    for suffix, prefix in (("Info/Maps", "official"),
                           ("Info/UGC/Maps", "workshop")):
        parent = root / suffix
        if parent.is_dir():
            sources += 1
            found.update(canonical_map_id(prefix + ":" + child.name)
                         for child in parent.iterdir() if child.is_dir())
    return found if sources else None


def validate_campaign_manifest(campaign, mode):
    if not isinstance(campaign, dict):
        return "Le serveur n'a pas transmis de manifeste valide."
    missions = campaign.get("missions")
    if not isinstance(missions, list):
        return "Le manifeste n'a pas de liste de missions."
    ids = set()
    available_modes = set()
    for mission in missions:
        if not isinstance(mission, dict):
            return "Une mission du manifeste est invalide."
        kind = mission.get("mode")
        if kind not in ("Invasion", "Conquest"):
            return "Une mission utilise un mode inconnu."
        map_id = mission.get("map_id")
        auto_invasion = (
            kind == "Invasion" and map_id == AUTO_INVASION_MAP_ID and
            mission.get("map_policy") == "local_seeded_invasion"
        )
        if not auto_invasion and canonical_map_id(map_id) is None:
            return ("Cette seed utilise un nom de carte plutôt qu'un ID "
                    "interne (official:<dossier> ou workshop:<dossier>). "
                    "Importe le nouvel APWorld et régénère la seed.")
        if not isinstance(mission.get("id"), str):
            return "Une mission ne possède pas d'identifiant."
        if mission["id"] in ids:
            return "Le manifeste contient une mission en double."
        ids.add(mission["id"])
        available_modes.add(kind)
    if not campaign.get("ready", False):
        return str(campaign.get("warning") or "La génération de la campagne a échoué.")
    expected = {"Invasion", "Conquest"} if mode == "Hybrid" else {mode}
    if not expected.issubset(available_modes):
        return ("Une campagne %s doit contenir des missions %s ; "
                "vérifie les catalogues de cartes du YAML." %
                (mode, " ET ".join(sorted(expected))))
    return None


def reconcile_local_maps(directory, campaign):
    """Annotate local folder evidence, without false-negative rejection."""
    installed = detect_installed_maps(directory)
    updated = dict(campaign)
    if installed is None:
        updated["local_map_scan"] = "unavailable"
        updated["local_map_scan_missing"] = []
        return updated
    selected = [m.get("map_id") or m.get("map") for m in
                campaign.get("missions", []) if isinstance(m, dict)]
    missing = sorted(x for x in selected
                     if isinstance(x, str) and canonical_map_id(x) not in installed)
    updated["local_map_scan"] = ("folders_found" if not missing
                                  else "requires_unity_asset_validation")
    updated["local_map_scan_missing"] = missing
    if missing:
        LOG.info("AP map folder scan cannot confirm %s. "
                 "Not blocking: the Unity asset registry is authoritative.", missing)
    return updated

def native_invasion_catalog(directory):
    """Read Castle Story's native asset discovery, never translated UI labels.

    Returns None until the Unity map scanner completed. An empty list after
    completion means that no compatible map could be identified safely.
    """
    data = load_json(Path(directory) / "installed_maps.json")
    if not isinstance(data, dict) or data.get("scan_complete") is not True:
        return None
    universe = {canonical_map_id(x) for x in data.get("map_ids", [])
                if canonical_map_id(x)}
    found = {}
    for value in data.get("invasion_map_ids", []):
        name = canonical_map_id(value)
        if name and name in universe:
            found.setdefault(name, value)
    return [found[key] for key in sorted(found)]


def seeded_local_map_choice(seed, player, candidates):
    """Stable across process restarts and game language changes."""
    pool = sorted(set(candidates), key=str.casefold)
    if not pool:
        raise ValueError("No verified native Invasion maps available")
    digest = hashlib.sha256(
        ("%s|%s|castle-story-invasion-map" % (seed, player)).encode("utf-8")
    ).digest()
    index = int.from_bytes(digest[:8], "big") % len(pool)
    return pool[index]


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

def wire_text(value):
    """Encode free-form UTF-8 without introducing newlines/tabs to wire rows."""
    return base64.b64encode(str(value if value is not None else "").encode("utf-8")).decode("ascii")


def campaign_wire_lines(token, protocol, status, message, campaign, default_mode):
    """Versioned, non-JSON snapshot for old Unity JsonUtility runtimes.

    Each fixed column is either ASCII numeric or base64(UTF-8). Missions are
    exactly seven tab-separated columns. Never serialize connection secrets.
    """
    missions = campaign.get("missions", []) if isinstance(campaign, dict) else []
    if not isinstance(missions, list):
        missions = []
    missing = campaign.get("missing_modes") or []
    if not isinstance(missing, list):
        missing = []
    lines = [
        "CASTLE_STORY_AP_CAMPAIGN_V1",
        str(token),
        str(int(protocol)),
        wire_text(status),
        wire_text(message),
        "1" if campaign.get("ready") is True else "0",
        wire_text(campaign.get("mode") or default_mode),
        wire_text(campaign.get("warning") or ""),
        wire_text(",".join(m for m in missing if m in ("Invasion", "Conquest"))),
        str(len(missions)),
    ]
    for mission in missions:
        if not isinstance(mission, dict):
            raise ValueError("Invalid campaign mission in wire snapshot")
        lines.append("\t".join([
            wire_text(mission.get("id")),
            wire_text(mission.get("mode")),
            wire_text(mission.get("map_id")),
            wire_text(mission.get("map")),
            str(int(mission.get("wave_goal") or 0)),
            str(int(mission.get("shard_start") or 0)),
            str(int(mission.get("shard_count") or 0)),
        ]))
    return "\n".join(lines) + "\n"


def save_campaign_wire(path, content):
    """Atomic replacement avoids Unity ever observing a partial manifest."""
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".tmp")
    temp.write_text(content, encoding="ascii")
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
        self.source_campaign = {}
        self.protocol_version = 0
        self.slot_data_provided = False
        self.slot_data_fields = []
        self.manifest_provided = False
        self.campaign_status = "unknown"
        self.campaign_message = ""
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
            "campaign_mission_count": len(self.campaign.get("missions", []))
                if isinstance(self.campaign, dict) and
                   isinstance(self.campaign.get("missions"), list) else -1,
            "protocol_version": self.protocol_version,
            "bridge_client_version": BRIDGE_CLIENT_VERSION,
            "updated_at": time.time(),
            "slot_data_provided": self.slot_data_provided,
            "slot_data_fields": self.slot_data_fields,
            "manifest_provided": self.manifest_provided,
            "campaign_status": self.campaign_status,
            "campaign_message": self.campaign_message,
            "connected": self.connected,
        }

    def persist_session(self):
        if self.token:
            campaign = self.campaign if isinstance(self.campaign, dict) else {}
            missions = campaign.get("missions")
            if not isinstance(missions, list):
                missions = []
            # Unity 5.6 may fail to deserialize a deeply nested missions[]
            # under session.campaign. Mirror it at the root in a standalone
            # file with the exact same token and a deterministic schema.
            # Write it before session.json: the session acts as the commit
            # marker for Unity's read loop.
            save_json(self.directory / "campaign_view.json", {
                "token": self.token,
                "protocol_version": self.protocol_version,
                "bridge_client_version": BRIDGE_CLIENT_VERSION,
                "campaign_status": self.campaign_status,
                "campaign_message": self.campaign_message,
                "manifest_provided": self.manifest_provided,
                "ready": campaign.get("ready") is True,
                "mode": campaign.get("mode", self.mode),
                "warning": campaign.get("warning", ""),
                "missing_modes": campaign.get("missing_modes") or [],
                "mission_count": len(missions),
                "missions": missions,
            })
            # Keep the versioned text snapshot adjacent to the JSON one. It is
            # deliberately simple to parse on Unity 5.6 / .NET 3.5.
            # Publish all manifests before session.json (our heartbeat marker).
            save_campaign_wire(
                self.directory / "campaign_wire.txt",
                campaign_wire_lines(
                    self.token, self.protocol_version, self.campaign_status,
                    self.campaign_message, campaign, self.mode,
                ),
            )
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
            self.slot_data_provided = isinstance(packet.get("slot_data"), dict)
            slot_data = packet.get("slot_data") if self.slot_data_provided else {}
            self.slot_data_fields = sorted(str(key) for key in slot_data)
            self.manifest_provided = "campaign" in slot_data
            mode = slot_data.get("game_mode", "Invasion")
            if mode not in IDS:
                raise RuntimeError(f"Unsupported Castle Story mode in slot data: {mode!r}")
            self.mode = mode
            self.protocol_version = int(slot_data.get("protocol_version") or 0)
            received_campaign = slot_data.get("campaign")
            if "campaign" not in slot_data:
                self.campaign = {}
                self.campaign_status = "missing"
                self.campaign_message = (
                    "Connecté à ArchiLAN, mais cette seed n'a pas de manifeste "
                    "de campagne. Installe le dernier APWorld dans ArchiLAN, "
                    "puis génère une NOUVELLE seed."
                )
            elif (not isinstance(received_campaign, dict) or
                  not isinstance(received_campaign.get("missions"), list)):
                self.campaign = {}
                self.campaign_status = "invalid"
                self.campaign_message = (
                    "Le serveur a fourni une campagne invalide. "
                    "Vérifie la version de l'APWorld Castle Story."
                )
            else:
                self.campaign = reconcile_local_maps(self.directory, received_campaign)
                problem = validate_campaign_manifest(self.campaign, mode)
                if problem:
                    self.campaign_status = "unready"
                    self.campaign_message = problem
                    self.campaign["ready"] = False
                    self.campaign["warning"] = problem
                else:
                    self.campaign_status = "ready"
                    self.campaign_message = ""
            LOG.info("AP slot_data: keys=%s protocol=%s campaign=%s missions=%s",
                     self.slot_data_fields, self.protocol_version,
                     self.campaign_status, len(self.campaign.get("missions", [])))
            if self.campaign_message:
                LOG.warning("[AP CAMPAIGN] %s", self.campaign_message)
            self.goal_wave = int(slot_data.get("goal_wave", 10))
            self.shard_goal = int(slot_data.get("conquest_capture_goal", 3))
            ids = slot_data.get("location_ids")
            self.allowed_ids = (set(x for x in ids if type(x) is int) & IDS[mode]
                                if isinstance(ids, list) else IDS[mode].copy())
            self.slot = int(packet["slot"])
            self.team = int(packet.get("team", 0))
            self.token = token_for(self.seed, self.slot, self.player, self.team)
            self.source_campaign = copy.deepcopy(self.campaign)
            self.connected = True
            self.refresh_dynamic_campaign()
            write_status(self.directory, "connected", "Connected to Archipelago as " + self.player)
            self.received = []
            self.seen_checks.clear()
            self.goal_sent = False
            self.persist_session()
            self.persist_items()
            LOG.info("Connected: slot=%s mode=%s seed=%s campaign_status=%s",
                     self.player, mode, self.seed, self.campaign_status)
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

    def refresh_dynamic_campaign(self):
        """Bind auto:invasion to one real native map, pinned per Archipelago seed.

        Until the game's New Game screen has enumerated its assets, keep the
        auto mission visible and request that the player open New Game.
        Never silently substitute an incompatible or localized map name.
        """
        if not self.connected or not self.token or not isinstance(self.source_campaign, dict):
            return False
        original = self.source_campaign.get("missions")
        if not isinstance(original, list) or not any(
                isinstance(m, dict) and m.get("map_id") == AUTO_INVASION_MAP_ID
                for m in original):
            return False

        campaign = copy.deepcopy(self.source_campaign)
        catalog = native_invasion_catalog(self.directory)
        state, message = "ready", ""
        if catalog is None:
            state = "waiting_for_game"
            message = ("Ouvre Nouvelle partie dans Castle Story pour charger les cartes "
                       "Invasion disponibles. Le jeu choisira ensuite la carte de la seed.")
        elif not catalog:
            state = "unready"
            message = ("Castle Story n'a pas exposé de carte compatible Invasion. "
                       "Ouvre Nouvelle partie et vérifie installed_maps.json / F9.")
            campaign["ready"] = False
            campaign["warning"] = message
        else:
            path = self.directory / "auto_map_selection.json"
            saved = load_json(path)
            previous = (
                saved.get("map_id") if isinstance(saved, dict) and
                saved.get("token") == self.token else None
            )
            if previous:
                selected = next(
                    (name for name in catalog
                     if canonical_map_id(name) == canonical_map_id(previous)),
                    None,
                )
                if selected is None:
                    state = "unready"
                    message = ("La carte choisie pour cette seed n'est plus "
                               "disponible : " + str(previous))
                    campaign["ready"] = False
                    campaign["warning"] = message
            else:
                selected = seeded_local_map_choice(
                    self.seed, self.player, catalog
                )
                save_json(path, {"token": self.token, "map_id": selected})
                LOG.info("Assigned local Invasion map to seed %s: %s",
                         self.seed, selected)
            if state == "ready":
                for mission in campaign["missions"]:
                    if mission.get("map_id") == AUTO_INVASION_MAP_ID:
                        mission["map_id"] = selected
                        mission["map"] = selected
                        mission["resolved_from"] = "native_invasion_catalog"

        if (campaign != self.campaign or state != self.campaign_status or
                message != self.campaign_message):
            self.campaign = campaign
            self.campaign_status = state
            self.campaign_message = message
            self.persist_session()
            LOG.info("[AP CAMPAIGN] dynamic map state=%s: %s", state, message or
                     next((m.get("map_id") for m in campaign["missions"]
                           if m.get("mode") == "Invasion"), "?"))
            return True
        return False

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
                    effective_connection_request(
                        self.directory, getattr(self, "fallback_request", None)
                    ) != self.request_snapshot):
                raise ConnectionChanged()
            self.refresh_dynamic_campaign()
            # Heartbeat: Unity must not mistake yesterday's persisted
            # connected=true for a live server session.
            self.persist_session()
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

def effective_connection_request(directory, fallback=None):
    """Game menu takes precedence, even when bridge was started via CLI.

    A disabled connection.json is an explicit disconnect and MUST NOT
    silently reactivate the previous command-line server.
    """
    request_path = Path(directory) / "connection.json"
    if request_path.exists():
        return read_connection_request(directory)
    return fallback


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
        requested = effective_connection_request(directory, static)
        if requested is None:
            if bridge is not None:
                bridge.connected = False
                bridge.persist_session()
                bridge = None
            active = None
            write_status(directory, "idle", "Déconnecté. Ouvre Archipelago pour te reconnecter.")
            await asyncio.sleep(1)
            continue
        if requested != active:
            if bridge is not None:
                bridge.connected = False
                bridge.persist_session()
            active = requested
            bridge = Bridge(directory, requested[1], requested[2])
            bridge.request_snapshot = requested
            bridge.fallback_request = static
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
