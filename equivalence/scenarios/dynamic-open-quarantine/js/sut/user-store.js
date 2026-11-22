"use strict";

const fs = require("fs");

function loadUser(user) {
  return fs.readFileSync(`/var/log/${user}.log`, "utf8");
}

function appendLog(entry) {
  fs.appendFileSync("/var/log/app.log", entry);
}

module.exports = { loadUser, appendLog };
