#!/usr/bin/env bash

append_dynamic_log() {
    echo "entry" >> "/var/log/$User.log"
}

append_log() {
    echo "entry" >> "/var/log/app.log"
}
