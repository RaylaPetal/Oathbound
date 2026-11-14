/**
 * Structured, redacted logging. Every call site passes only aggregate/
 * non-identifying fields; as defense in depth this also refuses to emit any
 * field whose name suggests it might carry secrets, keys, payloads, or
 * character identity (spec: "Relay deployment is reproducible and
 * observable without sensitive logging"). A log-capture test (task 8.1)
 * asserts no emitted line ever contains one of these field names.
 */
// Live frames add the frame's nonce/text/character and the pair and device it moves between, so traffic can't be
// tied to a pairing from the logs.
const FORBIDDEN_FIELD_NAME_PATTERN =
  /signature|privatekey|private_key|secret|capability|ciphertext|plaintext|charactername|character_name|world|catalog|invitationid|requestid|^nonce$|^text$|^character$|pairid|devicekeyid|messageid/i;

export type LogFields = Record<string, string | number | boolean | null | undefined>;

export function logEvent(event: string, fields: LogFields = {}): void {
  const safeFields: LogFields = {};
  for (const [key, value] of Object.entries(fields)) {
    if (FORBIDDEN_FIELD_NAME_PATTERN.test(key)) {
      // Fail loudly in development rather than silently dropping a field a
      // developer expected to see; this should never fire in normal operation.
      throw new Error(`log field "${key}" looks like it may carry sensitive data; do not log it`);
    }
    safeFields[key] = value;
  }
  console.error(JSON.stringify({ event, ...safeFields, at: new Date().toISOString() }));
}
