#include <catch2/catch_test_macros.hpp>

TEST_CASE("appends log", "[store]")
{
    equiv::LogStore store;
    store.appendLog();
}
