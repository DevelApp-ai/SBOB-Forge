import subprocess


def run_git_status():
    subprocess.run(["git", "status"])
