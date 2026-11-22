def append_log(entry):
    with open("/var/log/app.log", "a") as f:
        f.write(entry)
