#!/usr/bin/env bats

@test "runs git status" {
    run_git_status
}

@test "fetches status" {
    fetch_status
}

@test "appends log" {
    append_log
}

@test "saves cache" {
    save_cache
}

@test "legacy path" {
    skip
    rm "/tmp/legacy-cache"
}
