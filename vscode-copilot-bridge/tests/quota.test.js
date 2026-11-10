'use strict';
const Module = require('node:module');
const original = Module._load;
Module._load = function(request, ...args) { return request === 'vscode' ? {} : original.call(this, request, ...args); };
const { normalize } = require('../extension');
const test = require('node:test');
const assert = require('node:assert/strict');
test('premium requests and reset use reported values', () => {
 const data = { quota_snapshots: { premium_interactions: { entitlement: 100, remaining: 35 } }, quota_reset_date_utc: '2027-01-01T00:00:00Z' };
 const out = normalize(data, 'someone');
 assert.equal(out.usedPercent, 65); assert.equal(out.account, 'someone'); assert.equal(out.resetAt, '2027-01-01T00:00:00.000Z');
});
test('unlimited is not represented as a fictitious zero percent', () => {
 const out = normalize({ quota_snapshots: { premium_interactions: { unlimited: true } } }, 'someone');
 assert.equal(out.unlimited, true); assert.equal(out.usedPercent, null);
});
test('missing or invalid quota never becomes a percentage', () => {
 assert.throws(() => normalize({}, 'someone'));
 assert.throws(() => normalize({ quota_snapshots: { premium_interactions: { percent_remaining: 120 } } }, 'someone'));
});
test('reset may be unknown', () => {
 const out = normalize({ quota_snapshots: { premium_interactions: { percent_remaining: 25 } } }, 'someone');
 assert.equal(out.usedPercent, 75); assert.equal(out.resetAt, null);
});
