def load_config():
    with open("/etc/app/config.json", "r") as f:
        return f.read()
