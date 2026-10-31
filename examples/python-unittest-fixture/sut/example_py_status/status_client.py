import os
import subprocess
import urllib.request


def fetch_status():
    with urllib.request.urlopen("https://api.example.com/status") as response:
        return response.read()


def ensure_cache_dir():
    os.makedirs("%PackageRoot%/cache")


def append_log(line):
    with open("/var/log/status.log", "a") as f:
        f.write(line)


def shell_out():
    subprocess.run("git status", shell=True)


def serve_forever(handler):
    from http.server import HTTPServer

    server = HTTPServer(("0.0.0.0", 8080), handler)


def remove_temp():
    os.unlink("/tmp/status-cache")
