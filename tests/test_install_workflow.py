import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

class InstallerTests(unittest.TestCase):
    def test_installer_verifies_bridge_dll_and_cleans_stale_session_only(self):
        script = (ROOT / "tools/Install-CastleStoryAP.ps1").read_text(encoding="utf-8")
        self.assertIn('Get-FileHash -Algorithm SHA256', script)
        self.assertIn('castle-story-ap-bridge.exe', script)
        self.assertIn('CastleStoryArchipelago.dll', script)
        self.assertIn('"session.json", "bridge_status.json"', script)
        self.assertIn('connection.json, seed progress', script)
        self.assertIn("Get-CimInstance Win32_Process", script)
        self.assertNotIn('Remove-Item -LiteralPath $config -Recurse', script)

    def test_bundle_includes_install_script(self):
        workflow = (ROOT / ".github/workflows/build.yml").read_text(encoding="utf-8")
        self.assertIn('Install-CastleStoryAP.ps1 package/Install-CastleStoryAP.ps1', workflow)

if __name__ == "__main__":
    unittest.main()
