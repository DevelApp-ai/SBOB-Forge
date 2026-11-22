"use strict";

const { execFile } = require("child_process");

function runGitStatus() {
  execFile("git", ["status"]);
}

module.exports = { runGitStatus };
