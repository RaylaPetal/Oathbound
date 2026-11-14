import { describe, expect, it, vi } from "vitest";
import { logEvent } from "../src/lib/log";

describe("logEvent redaction", () => {
  it("emits a structured line for aggregate-only fields", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    logEvent("scheduled_cleanup_complete", { expiredInvitations: 3, removedNonces: 10 });
    expect(spy).toHaveBeenCalledTimes(1);
    const line = JSON.parse(spy.mock.calls[0]![0] as string);
    expect(line.event).toBe("scheduled_cleanup_complete");
    expect(line.expiredInvitations).toBe(3);
    spy.mockRestore();
  });

  const forbiddenFieldNames = [
    "signature",
    "privateKey",
    "capabilitySecret",
    "ciphertextBase64Url",
    "plaintext",
    "characterName",
    "world",
    "catalogSnapshot",
    "invitationId",
    "requestId",
    "nonce",
    "text",
    "character",
    "pairIdHash",
    "deviceKeyId",
    "messageId",
  ];

  it.each(forbiddenFieldNames)("refuses to log a field named %s", (fieldName) => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    expect(() => logEvent("test_event", { [fieldName]: "value" })).toThrow();
    expect(spy).not.toHaveBeenCalled();
    spy.mockRestore();
  });
});
