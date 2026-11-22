"use strict";

const { runGitStatus } = require("../sut/tool-runner.js");

describe("tool runner", () => {
  it("runs git status", () => {
    runGitStatus();
  });
});
