#!/usr/bin/env bash

save_cache() {
    echo "$Payload" > "/var/lib/app/cache.json"
}
