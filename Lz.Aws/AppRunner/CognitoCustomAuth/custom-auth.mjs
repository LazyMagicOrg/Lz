// =====================================================================================================
//  custom-auth.mjs — Cognito Custom-Auth challenge trigger (the seller flow, Scutara M0-7).
//
//  ONE Lambda handler wired to all THREE custom-auth challenge triggers (Define/Create/Verify),
//  dispatching on triggerSource. It is deployed INLINE (Node.js, no build step, no npm deps) rather than as
//  a separate compiled project: the logic is a tiny event adapter, and a lean dedicated function keeps a
//  latency-critical trigger (Cognito's 5-second timeout) off the monolith's cold-start.
//
//  FAIL CLOSED is the whole posture: any null/empty/malformed/unexpected input is a DENY, never a silent
//  accept. The key check is CONSTANT-TIME (crypto.timingSafeEqual). Dependencies are only Node's built-in
//  `crypto` and the AWS SDK v3 bundled in the Node managed runtime — and the SDK is imported LAZILY (only in
//  Verify) so the pure decisions below import with zero dependencies and are unit-testable with `node --test`.
//
//  Config (env; the same file serves any tenancy/table):
//    VENDOR_CRED_TABLE      — DynamoDB table holding, per vendor user, the SHA-256 hex of their API key.
//    VENDOR_CRED_HASH_ATTR  — the hash attribute name (default: apiKeyHash).
//    VENDOR_CRED_KEY_ATTR   — the partition-key attribute name (default: username).
// =====================================================================================================

import { createHash, timingSafeEqual } from 'node:crypto';

const CUSTOM_CHALLENGE = 'CUSTOM_CHALLENGE';
const TABLE = process.env.VENDOR_CRED_TABLE ?? '';
const HASH_ATTR = process.env.VENDOR_CRED_HASH_ATTR ?? 'apiKeyHash';
const KEY_ATTR = process.env.VENDOR_CRED_KEY_ATTR ?? 'username';

// ---- PURE decisions (exported for tests; no AWS deps) ------------------------------------------------

/**
 * True IFF sha256(presentedApiKey) CONSTANT-TIME-equals the stored hex hash. Fails closed on any
 * null/empty/malformed/wrong-length input; never throws. `Buffer.from(x,'hex')` does not throw on bad hex —
 * it returns a short/empty buffer — so the length guard (SHA-256 is always 32 bytes) rejects every malformed
 * stored value, which also keeps timingSafeEqual on equal-length inputs (it throws otherwise).
 */
export function answerIsCorrect(presentedApiKey, storedHashHex) {
  if (!presentedApiKey || !storedHashHex) return false;
  const stored = Buffer.from(storedHashHex, 'hex');
  const computed = createHash('sha256').update(presentedApiKey, 'utf8').digest();
  if (stored.length !== computed.length) return false;
  return timingSafeEqual(computed, stored);
}

/**
 * The DefineAuthChallenge flow decision. Single-shot by design (an API key is presented once, not guessed):
 * no attempts yet ⇒ 'issueChallenge'; exactly one correct CUSTOM_CHALLENGE ⇒ 'issueTokens'; EVERYTHING else
 * (a wrong answer — no online retry, a wrong challenge type, or more than one attempt) ⇒ 'fail'.
 * @param {{challengeName: string, correct: boolean}[]} session
 * @returns {'issueChallenge'|'issueTokens'|'fail'}
 */
export function decide(session) {
  if (!session || session.length === 0) return 'issueChallenge';
  if (session.length !== 1) return 'fail';
  const only = session[0];
  return only.correct && only.challengeName === CUSTOM_CHALLENGE ? 'issueTokens' : 'fail';
}

// ---- The one runtime read (AWS SDK imported lazily, cached) ------------------------------------------

let _ddb;
async function getStoredHash(username) {
  try {
    const { DynamoDBClient, GetItemCommand } = await import('@aws-sdk/client-dynamodb');
    _ddb ??= new DynamoDBClient({});
    const out = await _ddb.send(new GetItemCommand({
      TableName: TABLE,
      Key: { [KEY_ATTR]: { S: username } },
      ProjectionExpression: '#h',
      ExpressionAttributeNames: { '#h': HASH_ATTR },
      ConsistentRead: true,
    }));
    return out.Item?.[HASH_ATTR]?.S ?? null;
  } catch {
    // A store OUTAGE fails closed: an unreadable hash is a DENY, never an accept. The agent retries; a
    // transient DynamoDB error never mints a token.
    return null;
  }
}

// ---- The Cognito handler ----------------------------------------------------------------------------

export const handler = async (event) => {
  const src = event?.triggerSource;
  const req = event?.request;
  const res = event?.response;
  // A malformed event is left untouched: on the challenge paths Cognito treats an unpopulated response as a
  // non-accept, so doing nothing fails closed rather than fabricating a decision.
  if (!src || !req || !res) return event;

  if (src === 'DefineAuthChallenge_Authentication') {
    // userNotFound fails closed — never issue tokens (or a challenge, an existence oracle) for a missing user.
    if (req.userNotFound === true) {
      res.issueTokens = false;
      res.failAuthentication = true;
      return event;
    }
    const session = (req.session ?? []).map((s) => ({
      challengeName: s?.challengeName ?? '',
      correct: s?.challengeResult === true,
    }));
    const flow = decide(session);
    res.issueTokens = flow === 'issueTokens';
    res.failAuthentication = flow === 'fail';
    if (flow === 'issueChallenge') res.challengeName = CUSTOM_CHALLENGE;
  } else if (src === 'CreateAuthChallenge_Authentication') {
    // Set up OUR challenge only — a prompt, no server secret (the client presents its API key; nothing about
    // it rides in the challenge).
    if (req.challengeName === CUSTOM_CHALLENGE) {
      res.publicChallengeParameters = { prompt: 'present-api-key' };
      res.privateChallengeParameters = {};
      res.challengeMetadata = 'API_KEY';
    }
  } else if (src === 'VerifyAuthChallengeResponse_Authentication') {
    if (req.userNotFound === true) {
      res.answerCorrect = false;
      return event;
    }
    const presented = req.challengeAnswer;
    const username = event.userName;
    res.answerCorrect = (!presented || !username || !TABLE)
      ? false
      : answerIsCorrect(presented, await getStoredHash(username));
  }

  return event;
};
