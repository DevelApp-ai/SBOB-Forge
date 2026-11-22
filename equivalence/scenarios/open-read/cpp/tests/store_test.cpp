#include <catch2/catch_test_macros.hpp>

TEST_CASE("loads config", "[store]")
{
    equiv::ConfigStore store;
    store.loadConfig();
}
