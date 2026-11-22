"use strict";

const { loadConfig } = require("../sut/config-store.js");

describe("config store", () => {
  it("loads config", () => {
    loadConfig();
  });
});
