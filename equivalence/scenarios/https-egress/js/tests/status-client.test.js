"use strict";

const { fetchStatus } = require("../sut/status-client.js");

describe("status client", () => {
  it("fetches status", () => {
    fetchStatus();
  });
});
