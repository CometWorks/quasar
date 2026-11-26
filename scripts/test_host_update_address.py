"""Check the real updater's control-address boundary without running an update."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    "host_update", Path(__file__).resolve().parents[1] / "Quasar/Assets/host-update.py")
updater = importlib.util.module_from_spec(spec)
try:
    spec.loader.exec_module(updater)
except ModuleNotFoundError as error:
    # The host updater runs on the hosts, which are POSIX. It takes a file lock
    # with fcntl, which does not exist on Windows, so import fails there.
    if error.name != "fcntl":
        raise
    raise unittest.SkipTest("host-update.py needs fcntl, which this platform does not have")


class HostUpdateAddressTests(unittest.TestCase):
    def test_download_paths_retain_hosted_prefix_and_local_root(self):
        for address in ("https://dash.se-servers.net/my-panel/", "https://quasar.example/", "http://127.0.0.1:8080/"):
            with self.subTest(address=address):
                base = updater.origin_url({"connection": {"quasarUrl": address}})
                self.assertEqual(base + "/api/v1/hosts/one/update/binary",
                                 address.rstrip("/") + "/api/v1/hosts/one/update/binary")

    def test_unapproved_paths_and_ambiguous_addresses_are_rejected(self):
        for address in ("https://other.example/my-panel/", "http://dash.se-servers.net/my-panel/",
                        "https://dash.se-servers.net/my-panel/../other/",
                        "https://dash.se-servers.net/my%2Fpanel/",
                        "https://dash.se-servers.net/my-panel/?redirect=other",
                        "https://user@dash.se-servers.net/my-panel/",
                        "https://dash.se-servers.net/my-panel/#other"):
            with self.subTest(address=address), self.assertRaises(ValueError):
                updater.origin_url({"connection": {"quasarUrl": address}})


if __name__ == "__main__":
    unittest.main()
