// Run: node --test custom-auth.test.mjs   (no npm/deps — Node's built-in test runner over the pure decisions)
//
// Pins the security core of the Custom-Auth trigger: the constant-time key check and the single-shot flow.
// Only the PURE exports are imported, so the AWS SDK (lazily imported inside Verify) is never loaded here.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { answerIsCorrect, decide } from './custom-auth.mjs';

const sha256Hex = (s) => createHash('sha256').update(s, 'utf8').digest('hex');

// ---- answerIsCorrect — the constant-time key check ----

test('answerIsCorrect: the matching key is true', () => {
  const key = 'vk_live_9f3c_seller_acme_0d1e2f';
  assert.equal(answerIsCorrect(key, sha256Hex(key)), true);
});

test('answerIsCorrect: a wrong key is false', () => {
  assert.equal(answerIsCorrect('wrong-key', sha256Hex('the-real-key')), false);
});

test('answerIsCorrect: no presented key is false', () => {
  assert.equal(answerIsCorrect(null, sha256Hex('k')), false);
  assert.equal(answerIsCorrect('', sha256Hex('k')), false);
});

test('answerIsCorrect: no stored hash is false', () => {
  assert.equal(answerIsCorrect('k', null), false);
  assert.equal(answerIsCorrect('k', ''), false);
});

test('answerIsCorrect: a malformed stored hash is false, never throws', () => {
  assert.equal(answerIsCorrect('k', 'not-hex-zzzz'), false);
  assert.equal(answerIsCorrect('k', 'abc'), false); // odd length
});

test('answerIsCorrect: a wrong-length stored hash is false', () => {
  assert.equal(answerIsCorrect('k', '00112233445566'), false);
});

test('answerIsCorrect: hex parse is case-insensitive, but the key is exact', () => {
  const key = 'MyKey';
  const upperHex = createHash('sha256').update(key, 'utf8').digest('hex').toUpperCase();
  assert.equal(answerIsCorrect(key, upperHex), true);
  assert.equal(answerIsCorrect('mykey', sha256Hex(key)), false);
});

// ---- decide — the single-shot flow ----

test('decide: no attempts issues the challenge', () => {
  assert.equal(decide([]), 'issueChallenge');
});

test('decide: a null session issues the challenge', () => {
  assert.equal(decide(null), 'issueChallenge');
});

test('decide: one correct CUSTOM_CHALLENGE issues tokens', () => {
  assert.equal(decide([{ challengeName: 'CUSTOM_CHALLENGE', correct: true }]), 'issueTokens');
});

test('decide: one wrong answer fails, and does not re-issue the challenge', () => {
  assert.equal(decide([{ challengeName: 'CUSTOM_CHALLENGE', correct: false }]), 'fail');
});

test('decide: a correct answer to the wrong challenge type fails', () => {
  assert.equal(decide([{ challengeName: 'SRP_A', correct: true }]), 'fail');
});

test('decide: more than one attempt fails (single-shot)', () => {
  assert.equal(
    decide([
      { challengeName: 'CUSTOM_CHALLENGE', correct: false },
      { challengeName: 'CUSTOM_CHALLENGE', correct: true },
    ]),
    'fail',
  );
});
