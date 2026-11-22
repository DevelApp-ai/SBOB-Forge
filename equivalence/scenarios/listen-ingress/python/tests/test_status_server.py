from equiv_core import status_server


def test_starts_listener(sock):
    status_server.start_listener(sock)
