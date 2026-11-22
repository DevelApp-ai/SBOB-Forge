from equiv_core import user_store


def test_loads_user():
    user_store.load_user("alice")


def test_appends_log():
    user_store.append_log("entry")
