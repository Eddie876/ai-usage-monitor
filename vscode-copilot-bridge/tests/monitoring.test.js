'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const fs = require('node:fs/promises');
const https = require('node:https');
const Module = require('node:module');
const os = require('node:os');
const path = require('node:path');

test('the bridge pauses and resumes monitoring without reconnecting', { timeout: 10000 }, async t => {
  const previousLocalAppData = process.env.LOCALAPPDATA;
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'ai-usage-bridge-'));
  process.env.LOCALAPPDATA = temp;
  const directory = path.join(temp, 'AiUsageWidget');
  const settings = path.join(directory, 'sources.json');
  const cache = path.join(directory, 'copilot-usage.json');
  await fs.mkdir(directory);
  await fs.writeFile(settings, '\uFEFF{"copilot":false}');

  const commands = new Map();
  const messages = [];
  let authCalls = 0;
  let connectionUpdates = 0;
  let scheduledRefresh;
  const vscode = {
    commands: { registerCommand: (name, handler) => commands.set(name, handler) },
    authentication: {
      getSession: async () => {
        authCalls++;
        return { id: 'session', accessToken: 'test-token', account: { label: 'Test account' } };
      },
      onDidChangeSessions: () => ({ dispose() {} })
    },
    window: { showInformationMessage: message => messages.push(message) }
  };
  const originalLoad = Module._load;
  let bridge;
  t.after(async () => {
    bridge?.deactivate();
    if (previousLocalAppData === undefined) delete process.env.LOCALAPPDATA;
    else process.env.LOCALAPPDATA = previousLocalAppData;
    await fs.rm(temp, { recursive: true, force: true });
  });
  Module._load = function (request, ...args) {
    return request === 'vscode' ? vscode : originalLoad.call(this, request, ...args);
  };
  try { bridge = require('../extension'); }
  finally { Module._load = originalLoad; }

  t.mock.method(globalThis, 'setInterval', (callback, delay) => {
    assert.equal(delay, 60000);
    scheduledRefresh = callback;
  });
  let requestCount = 0;
  let onRequest;
  t.mock.method(https, 'get', (_url, _options, callback) => {
    requestCount++;
    onRequest((percent, status = 200) => {
      const response = new EventEmitter();
      response.statusCode = status;
      response.headers = {};
      response.resume = () => {};
      callback(response);
      response.emit('data', Buffer.from(JSON.stringify({ quota_snapshots: { premium_interactions: { percent_remaining: percent } } })));
      response.emit('end');
    });
    return new EventEmitter();
  });

  await bridge.activate({
    globalState: { get: () => true, update: async () => { connectionUpdates++; } },
    subscriptions: []
  });
  const manualRefresh = commands.get('aiUsageWidget.refreshCopilot');
  await scheduledRefresh();
  await manualRefresh();
  await commands.get('aiUsageWidget.connectCopilot')();
  assert.equal(authCalls, 0);
  assert.equal(requestCount, 0);
  assert.equal(connectionUpdates, 0);
  assert.match(messages[0], /監控已暫停/);
  await assert.rejects(fs.access(cache), { code: 'ENOENT' });

  async function replyTo(refresh, percent, { beforeReply, status } = {}) {
    const started = new Promise(resolve => { onRequest = resolve; });
    const refreshing = refresh();
    const reply = await started;
    if (beforeReply) await beforeReply();
    reply(percent, status);
    await refreshing;
  }

  await fs.writeFile(settings, '{"copilot":true}');
  await replyTo(scheduledRefresh, 60);
  const previous = await fs.readFile(cache, 'utf8');
  assert.equal(JSON.parse(previous).usedPercent, 40);

  const pause = () => fs.writeFile(settings, '{"copilot":false}');
  await replyTo(manualRefresh, 10, { beforeReply: pause });
  assert.equal(await fs.readFile(cache, 'utf8'), previous);

  await fs.writeFile(settings, '{"copilot":"false"}');
  await replyTo(manualRefresh, 80);
  assert.equal(JSON.parse(await fs.readFile(cache, 'utf8')).usedPercent, 20);
  assert.equal(connectionUpdates, 0);

  const writeFile = fs.writeFile;
  const stagedWrite = t.mock.method(fs, 'writeFile', async (file, ...args) => {
    await writeFile(file, ...args);
    if (path.extname(file) === '.tmp') await pause();
  });
  await replyTo(manualRefresh, 10);
  stagedWrite.mock.restore();
  assert.equal(JSON.parse(await fs.readFile(cache, 'utf8')).usedPercent, 20);
  assert.deepEqual((await fs.readdir(directory)).sort(), ['copilot-usage.json', 'sources.json']);

  await fs.writeFile(settings, '{"copilot":true}');
  await replyTo(manualRefresh, 0, { beforeReply: pause, status: 429 });
  assert.equal(JSON.parse(await fs.readFile(cache, 'utf8')).usedPercent, 20);
  await fs.writeFile(settings, '{"copilot":true}');
  await manualRefresh();
  assert.equal(requestCount, 5);
});
