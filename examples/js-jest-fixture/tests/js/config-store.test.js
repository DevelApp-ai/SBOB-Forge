"use strict";

const fs = require("fs");

const { loadConfig, loadUser, runGitStatus } = require("../sut/js/config-store.js");

describe("config store", () => {
  it("loads config", () => {
    loadConfig();
  });

  it.skip("legacy path", () => {
    loadUser("legacy");
  });

  test.each(["/var/log/app.log", "/var/log/other.log"])("writes log %s", (p) => {
    fs.appendFileSync(p, "entry");
  });

  test("runs git status", () => {
    runGitStatus();
  });
});
