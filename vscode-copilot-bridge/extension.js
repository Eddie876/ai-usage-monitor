'use strict';
const vscode = require('vscode');
const fs = require('node:fs/promises');
const path = require('node:path');
const https = require('node:https');
const crypto = require('node:crypto');
const directory = path.join(process.env.LOCALAPPDATA || '', 'AiUsageWidget');
const output = path.join(directory, 'copilot-usage.json');
const sourcesFile = path.join(directory, 'sources.json');
const scope = ['user:email'];
let refreshing = false;
let nextAllowed = 0;
let enabled = false;
let epoch = 0;
let interval;
let writes = Promise.resolve();

async function isMonitoringCopilot() {
  if (!process.env.LOCALAPPDATA) return false;
  try {
    const contents = await fs.readFile(sourcesFile, 'utf8');
    const sources = JSON.parse(contents.replace(/^\uFEFF/, ''));
    return sources?.copilot !== false;
  } catch (error) {
    // Missing and malformed settings use the same defaults as the widget.
    return error.code === 'ENOENT' || error instanceof SyntaxError;
  }
}

async function canRefresh(generation) {
  return await isMonitoringCopilot() && enabled && generation === epoch;
}

function write(value, { generation = epoch, requiresMonitoring = false } = {}) {
  // Serialize file replacement so a logout/account change cannot be overwritten
  // by an older in-flight refresh after its invalidation has been published.
  const operation = writes.catch(() => {}).then(() => writeCurrent(value, generation, requiresMonitoring));
  writes = operation;
  return operation;
}
async function writeCurrent(value, generation, requiresMonitoring) {
  if (generation !== epoch) return;
  if (requiresMonitoring && !await canRefresh(generation)) return;
  if (!process.env.LOCALAPPDATA) throw new Error('LOCALAPPDATA unavailable');
  await fs.mkdir(directory, { recursive: true });
  const tmp = path.join(directory, `copilot-${process.pid}-${crypto.randomBytes(6).toString('hex')}.tmp`);
  try {
    await fs.writeFile(tmp, JSON.stringify(value), { mode: 0o600 });
    if (requiresMonitoring && !await isMonitoringCopilot()) return;
    if (generation === epoch) await fs.rename(tmp, output);
  }
  finally { await fs.rm(tmp, { force: true }).catch(() => {}); }
}
function fetchQuota(token) {
  return new Promise((resolve, reject) => {
    const req = https.get('https://api.github.com/copilot_internal/user', {
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/json', 'User-Agent': 'ai-usage-widget' }, timeout: 15000,
    }, response => {
      const status = response.statusCode || 0;
      if (status !== 200) {
        if (status === 429 || status === 403) {
          const raw = response.headers['retry-after'];
          const seconds = Number(raw);
          const parsed = typeof raw === 'string' ? Date.parse(raw) : NaN;
          nextAllowed = Math.max(Date.now() + 4 * 60000, Number.isFinite(seconds) && seconds > 0 ? Date.now() + seconds * 1000 : Number.isFinite(parsed) ? parsed : 0);
        }
        response.resume(); return reject(new Error(`GitHub 額度查詢未成功（${status}）。`));
      }
      let chunks = [], size = 0;
      response.on('data', b => { size += b.length; if (size > 1024 * 1024) { req.destroy(); return reject(new Error('GitHub 回應過大。')); } chunks.push(b); });
      response.on('end', () => { try { resolve(JSON.parse(Buffer.concat(chunks).toString('utf8'))); } catch { reject(new Error('GitHub 額度資料格式無效。')); } });
      response.on('error', reject);
    });
    req.on('timeout', () => req.destroy(new Error('GitHub 額度查詢逾時。')));
    req.on('error', reject);
  });
}
function normalize(data, login) {
  const q = data?.quota_snapshots?.premium_interactions;
  if (!q || typeof q !== 'object') throw new Error('此帳號沒有回傳 premium requests 額度。');
  const unlimited = q.unlimited === true;
  const entitlement = q.entitlement;
  const remaining = typeof q.quota_remaining === 'number' ? q.quota_remaining : q.remaining;
  let percent = typeof q.percent_remaining === 'number' ? q.percent_remaining : (typeof remaining === 'number' && typeof entitlement === 'number' && entitlement > 0 ? 100 * remaining / entitlement : null);
  if (!unlimited && (percent === null || !Number.isFinite(percent) || percent < 0 || percent > 100)) throw new Error('Copilot 額度資料不完整。');
  const raw = data.quota_reset_date_utc ?? data.quota_reset_date;
  let reset = null;
  if (typeof raw === 'string' && Number.isFinite(Date.parse(raw))) reset = new Date(raw).toISOString();
  return { version: 1, account: login, observedAt: new Date().toISOString(), usedPercent: unlimited ? null : 100 - percent, unlimited, resetAt: reset, plan: typeof data.copilot_plan === 'string' ? data.copilot_plan : null };
}
async function refresh(forceInteractive = false) {
  if (refreshing || !enabled || Date.now() < nextAllowed) return;
  const generation = epoch;
  const writeReading = value => write(value, { generation, requiresMonitoring: true });
  refreshing = true;
  try {
    if (!await canRefresh(generation)) return;
    const session = await vscode.authentication.getSession('github', scope, forceInteractive ? { createIfNone: true } : { silent: true });
    if (!await canRefresh(generation)) return;
    if (!session) { await writeReading({ version: 1, error: '請在 VS Code 執行「AI 額度：連接 VS Code 的 GitHub 帳號」。' }); return; }
    const data = await fetchQuota(session.accessToken);
    if (!await canRefresh(generation)) return;
    const after = await vscode.authentication.getSession('github', scope, { silent: true });
    if (!enabled || epoch !== generation || !after || after.id !== session.id) return;
    await writeReading(normalize(data, session.account.label));
  } catch (e) {
    if (enabled && epoch === generation) await writeReading({ version: 1, error: e instanceof Error && /^GitHub |^Copilot |^此帳號/.test(e.message) ? e.message : '無法取得 Copilot 額度，請檢查 GitHub 登入。' }).catch(() => {});
  } finally { refreshing = false; }
}
async function activate(context) {
  enabled = context.globalState.get('connected', false);
  context.subscriptions.push(vscode.commands.registerCommand('aiUsageWidget.connectCopilot', async () => {
    if (!await isMonitoringCopilot()) {
      vscode.window.showInformationMessage('Copilot 監控已暫停，請先在 AI 額度的「監控來源」勾選 GitHub Copilot。');
      return;
    }
    enabled = true;
    const generation = ++epoch;
    await context.globalState.update('connected', true);
    if (!enabled || generation !== epoch) return;
    await refresh(true);
    if (enabled && generation === epoch) vscode.window.showInformationMessage('已嘗試連接 Copilot；請在 AI 額度視窗查看狀態。');
  }));
  context.subscriptions.push(vscode.commands.registerCommand('aiUsageWidget.disconnectCopilot', async () => {
    enabled = false;
    const generation = ++epoch;
    await context.globalState.update('connected', false);
    await write({ version: 1, error: '已中斷 Copilot 連接。' }, { generation }).catch(() => {});
  }));
  context.subscriptions.push(vscode.commands.registerCommand('aiUsageWidget.refreshCopilot', () => refresh(false)));
  context.subscriptions.push(vscode.authentication.onDidChangeSessions(e => { if (e.provider.id === 'github' && enabled) { epoch++; write({ version: 1, error: 'GitHub 登入已變更，正在重新確認。' }).catch(() => {}); refresh(false); } }));
  interval = setInterval(() => refresh(false), 60 * 1000);
  context.subscriptions.push({ dispose: () => clearInterval(interval) });
  if (enabled) await refresh(false);
}
function deactivate() { enabled = false; epoch++; if (interval) clearInterval(interval); }
module.exports = { activate, deactivate, normalize };
