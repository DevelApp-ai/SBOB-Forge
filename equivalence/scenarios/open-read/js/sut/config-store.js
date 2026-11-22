"use strict";

const fs = require("fs");

function loadConfig() {
  return fs.readFileSync("/etc/app/config.json", "utf8");
}

module.exports = { loadConfig };
