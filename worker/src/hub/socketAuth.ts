import { b64UrlRandom } from "./random";

/** protocol/constants.json `socketAuth.baseString`. */
export function socketAuthBaseString(deviceKeyId: string, challengeNonce: string): string {
  return `oathbound-socket-v1\n${deviceKeyId}\n${challengeNonce}`;
}

export function newChallengeNonce(): string {
  return b64UrlRandom(16);
}
