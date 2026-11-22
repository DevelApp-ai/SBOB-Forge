import { describe, it } from "vitest";

import { saveCache } from "../sut/store";

describe("cache store", () => {
  it("saves cache", () => {
    saveCache("{}");
  });
});
