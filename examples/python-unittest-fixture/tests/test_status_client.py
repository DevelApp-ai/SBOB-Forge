import unittest

from example_py_status import status_client


class StatusClientTests(unittest.TestCase):
    def test_fetch_status(self):
        status = status_client.fetch_status()

    @unittest.skipIf(False, "not flaky enough to skip")
    def test_appends_log(self):
        status_client.append_log("entry")

    @unittest.skip("legacy harness")
    def test_legacy_shell(self):
        status_client.shell_out()
