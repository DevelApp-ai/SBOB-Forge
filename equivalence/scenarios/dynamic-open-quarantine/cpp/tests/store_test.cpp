#include <catch2/catch_test_macros.hpp>

TEST_CASE("appends log", "[store]")
{
    equiv::UserStore store;
    store.appendLog();
}

TEST_CASE("loads user log", "[store]")
{
    equiv::UserStore store;
    store.loadUser("alice");
}
