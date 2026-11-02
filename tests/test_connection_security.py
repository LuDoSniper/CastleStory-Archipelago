"""Secure native in-game login and no stored Archipelago passwords."""
from pathlib import Path
import unittest

from test_bridge import bridge

ROOT = Path(__file__).resolve().parents[1]


class InMemoryBridgeCredentials(unittest.TestCase):
    def test_stdin_connect_and_disconnect_without_disk(self):
        control = bridge.StdinControl(start_reader=False)
        control.apply({
            "cmd": "connect", "server": "archilan.example.org:38281",
            "slot": "Tester", "password": "PRIVATE-PASSWORD"
        })
        self.assertEqual(control.request,
                         ("ws://archilan.example.org:38281", "Tester",
                          "PRIVATE-PASSWORD"))
        control.apply({"cmd": "disconnect"})
        self.assertIsNone(control.request)

    def test_stdin_control_drains_new_commands_atomically(self):
        control = bridge.StdinControl(start_reader=False)
        control.queue.put({"cmd": "connect", "server": "localhost:1",
                           "slot": "Old", "password": "old"})
        control.queue.put({"cmd": "connect", "server": "localhost:2",
                           "slot": "New", "password": "new"})
        self.assertEqual(control.drain(), ("ws://localhost:2", "New", "new"))
        control.queue.put({"cmd": "disconnect"})
        self.assertIsNone(control.drain())

    def test_unity_never_writes_password_to_persistent_json(self):
        menu = (ROOT / "mod/ArchipelagoMenu.cs").read_text(encoding="utf-8")
        plugin = (ROOT / "mod/CastleStoryArchipelagoPlugin.cs").read_text(encoding="utf-8")
        self.assertIn("connection_preferences.json", menu)
        self.assertIn("LoadRememberedConnection();", plugin)
        self.assertIn("if (File.Exists(oldFile)) File.Delete(oldFile);", menu)
        self.assertIn("info.RedirectStandardInput = true;", menu)
        self.assertIn("--stdin-control --bridge-dir", menu)
        self.assertIn("localBridge.StandardInput.WriteLine(payload)", menu)
        self.assertNotIn('File.WriteAllText(Path.Combine(directory, "connection.json")', menu)
        self.assertNotIn("public string password;\n        }\n\n        private void LoadRememberedConnection", menu)
        self.assertIn("loginPassword = \"\";", menu)

    def test_bridge_stdin_path_does_not_read_connection_file(self):
        src = (ROOT / "client/bridge.py").read_text(encoding="utf-8")
        self.assertIn("class StdinControl:", src)
        self.assertIn("stdin_control", src)
        self.assertIn("control.drain() if control is not None", src)
        self.assertIn("threading.Thread(target=self._read, daemon=True)", src)


if __name__ == "__main__":
    unittest.main()
