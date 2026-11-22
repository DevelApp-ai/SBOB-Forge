import { describe, it } from "vitest";

import { loadConfig } from "../sut/store";

describe("config store", () => {
  it("loads config", () => {
    loadConfig();
  });
});
