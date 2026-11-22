#!/usr/bin/env bats

@test "appends dynamic log" {
    append_dynamic_log
}

@test "appends app log" {
    append_log
}
