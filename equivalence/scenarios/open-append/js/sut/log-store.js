"use strict";

const fs = require("fs");

function appendLog(entry) {
  fs.appendFileSync("/var/log/app.log", entry);
}

module.exports = { appendLog };
