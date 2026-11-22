def load_user(user):
    with open(f"/var/log/{user}.log") as f:
        return f.read()


def append_log(entry):
    with open("/var/log/app.log", "a") as f:
        f.write(entry)
