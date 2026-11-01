#include <stdio.h>

TEST(ConfigStore, OpensConfigReadOnly)
{
    FILE *f = open_config();
    (void)f;
}

TEST(ConfigStore, WritesCache)
{
    save_cache("payload");
}

TEST(ConfigStore, AppendsLog)
{
    append_log();
}

TEST(ConfigStore, RunsGitStatus)
{
    run_git_status();
}

TEST(ConfigStore, SkippedLegacyPath)
{
    GTEST_SKIP();
    fopen("/etc/app/legacy.json", "r");
}

static void legacy_probe(void)
{
    system("ls");
}
