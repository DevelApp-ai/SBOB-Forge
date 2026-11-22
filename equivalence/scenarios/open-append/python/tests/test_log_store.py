from equiv_core import log_store


def test_appends_log():
    log_store.append_log("entry")
