#include <catch2/catch_test_macros.hpp>

TEST_CASE("fetches status", "[client]")
{
    equiv::StatusClient client;
    client.syncRemote();
}
