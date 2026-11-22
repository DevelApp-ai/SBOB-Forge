#include <catch2/catch_test_macros.hpp>

TEST_CASE("starts listener", "[server]")
{
    equiv::StatusServer server;
    server.startListener();
}
