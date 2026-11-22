"use strict";

const { startListener } = require("../sut/status-server.js");

describe("status server", () => {
  it("starts listener", () => {
    startListener();
  });
});
