import pytest

from example_py_core import config_store


def test_loads_config():
    config = config_store.load_config()


@pytest.mark.skip
def test_legacy_path():
    with open("/etc/app/legacy.json") as f:
        pass


@pytest.mark.parametrize("path", ["/var/log/app.log", "/var/log/other.log"])
def test_writes_log(path):
    with open(path, "w") as f:
        f.write("entry")
