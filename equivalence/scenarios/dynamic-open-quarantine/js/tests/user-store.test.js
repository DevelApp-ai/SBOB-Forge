"use strict";

const { loadUser, appendLog } = require("../sut/user-store.js");

describe("user store", () => {
  it("loads user", () => {
    loadUser("alice");
  });

  it("appends log", () => {
    appendLog("entry");
  });
});
