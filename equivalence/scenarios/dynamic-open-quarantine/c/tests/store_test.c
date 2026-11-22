TEST(UserStore, AppendsLog)
{
    FILE *f = append_log();
    (void)f;
}

TEST(UserStore, AppendsDynamicLog)
{
    FILE *f = append_dynamic_log("alice");
    (void)f;
}
