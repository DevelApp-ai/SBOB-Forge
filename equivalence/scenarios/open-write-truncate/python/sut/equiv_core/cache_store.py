def save_cache(json):
    with open("/var/lib/app/cache.json", "w") as f:
        f.write(json)
