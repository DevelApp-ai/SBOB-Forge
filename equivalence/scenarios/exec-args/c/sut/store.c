int run_git_status(void)
{
    execvp("git", (char *[]){"/usr/bin/git", "status", NULL});
    return 0;
}
