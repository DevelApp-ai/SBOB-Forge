#include <catch2/catch_test_macros.hpp>

TEST_CASE("saves cache", "[store]")
{
    equiv::CacheStore store;
    store.saveCache();
}
