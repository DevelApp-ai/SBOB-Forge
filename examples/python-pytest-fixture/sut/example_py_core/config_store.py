import os
import subprocess

import requests


def load_config():
    with open("/etc/app/config.json") as f:
        return f.read()


def save_cache(json):
    with open("%PackageRoot%/cache/last.json", "w") as f:
        f.write(json)


def load_user(user):
    with open(f"/etc/app/users/{user}.json") as f:
        return f.read()


def run_git_status():
    subprocess.run(["git", "status"])


def clean_cache():
    os.remove("%PackageRoot%/cache/last.json")


def fetch_status():
    return requests.get("https://api.example.com/status")


def start_listener(sock):
    sock.bind(("0.0.0.0", 8080))
