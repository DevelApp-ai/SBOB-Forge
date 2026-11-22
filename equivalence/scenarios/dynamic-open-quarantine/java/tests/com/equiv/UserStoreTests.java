package com.equiv.tests;

import com.equiv.sut.UserStore;
import org.junit.jupiter.api.Test;

public class UserStoreTests
{
    @Test
    public void loadsUser() throws Exception
    {
        new UserStore().loadUser("alice");
    }

    @Test
    public void appendsLog() throws Exception
    {
        new UserStore().appendLog("entry");
    }
}
