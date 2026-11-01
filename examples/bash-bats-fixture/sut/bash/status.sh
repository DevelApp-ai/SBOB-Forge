#!/usr/bin/env bash

run_git_status() {
    git status
}

fetch_status() {
    curl "https://api.example.com/status"
}

append_log() {
    echo "entry" >> "/var/log/bash-app.log"
}

save_cache() {
    echo "$Payload" > "%PackageRoot%/cache/last.json"
}

append_dynamic_log() {
    echo "entry" >> "/var/log/$Name.log"
}

clean_cache() {
    rm "%PackageRoot%/cache/last.json"
}
