"use strict";

const { appendLog } = require("../sut/log-store.js");

describe("log store", () => {
  it("appends log", () => {
    appendLog("entry");
  });
});
