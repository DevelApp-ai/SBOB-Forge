"use strict";

const fs = require("fs");

function saveCache(data) {
  fs.writeFileSync("/var/lib/app/cache.json", data);
}

module.exports = { saveCache };
