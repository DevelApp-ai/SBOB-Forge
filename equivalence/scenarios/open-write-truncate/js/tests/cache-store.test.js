"use strict";

const { saveCache } = require("../sut/cache-store.js");

describe("cache store", () => {
  it("saves cache", () => {
    saveCache("{}");
  });
});
