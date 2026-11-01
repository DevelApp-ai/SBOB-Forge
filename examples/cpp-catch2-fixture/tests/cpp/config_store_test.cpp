#include <catch2/catch_test_macros.hpp>

TEST_CASE("loads config", "[store]")
{
    ConfigStore store;
    store.loadConfig();
}

TEST_CASE("writes cache", "[store]")
{
    ConfigStore store;
    store.saveCache();
}

TEST_CASE("appends log", "[store]")
{
    ConfigStore store;
    store.appendLog();
}

TEST_CASE("opens database", "[store]")
{
    ConfigStore store;
    store.openDatabase();
}

TEST_CASE("runs command", "[store]")
{
    ConfigStore store;
    store.runCommand();
}

TEST_CASE("legacy path is retired", "[store]")
{
    SKIP("legacy path is retired");
    std::system("false");
}

static void legacy_probe()
{
    std::system("ls");
}
