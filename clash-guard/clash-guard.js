#!/usr/bin/env node
'use strict';

/**
 * Clash Verge 节点守护，由 Windows 计划任务定期运行。
 * 「节点选择」只选带 🇸🇬 前缀的真实代理节点。全部不可用时直连，恢复后切回。
 * --verbose 输出检查过程；--dry-run 只检查，不写代理选择、日志或状态文件。
 * 退出码：0 正常或跳过，1 切换或运行失败，2 控制器 API 异常，3 另一实例在运行。
 */

const fs = require('node:fs');
const http = require('node:http');
const net = require('node:net');
const path = require('node:path');

const DEFAULT_TEST_URL = 'https://www.gstatic.com/generate_204';
const NO_COOLDOWN = process.env.CLASH_GUARD_NO_COOLDOWN === '1';
const CFG = {
  api: {
    host: '127.0.0.1',
    port: 9097,
    secret: process.env.CLASH_GUARD_SECRET ?? '',
  },
  group: '节点选择',
  directName: 'DIRECT',
  testUrl: process.env.CLASH_GUARD_TEST_URL || DEFAULT_TEST_URL,
  judge: { timeoutMs: 6000, maxDelay: 1500 },
  confirmGapMs: 30000,
  scan: { concurrency: 8, timeoutMs: 5000, maxDelay: 1000 },
  cooldownMs: NO_COOLDOWN ? 0 : 10 * 60 * 1000,
  restoreCooldownMs: NO_COOLDOWN ? 0 : 2 * 60 * 1000,
  badTtlMs: 30 * 60 * 1000,
  offLogEveryMs: 30 * 60 * 1000,
  logPath: path.join(__dirname, '日志-clash-switch.log'),
  statePath: path.join(__dirname, '状态-clash-guard.json'),
  pausePath: path.join(__dirname, '暂停-clash-guard.json'),
  pauseLogEveryMs: 30 * 60 * 1000,
};

const SKIP_TYPES = new Set([
  'Direct', 'Reject', 'RejectDrop', 'Compatible', 'Pass', 'Dns',
  'Selector', 'URLTest', 'Fallback', 'LoadBalance', 'Relay', 'Smart',
]);
const INFO_NODE_PAT = /(剩余流量|下次重置|套餐到期|官网)/;
const ARG = process.argv.slice(2);
const VERBOSE = ARG.includes('--verbose') || ARG.includes('-v');
const DRY = ARG.includes('--dry-run');

class ControllerError extends Error {}

function ts(d = new Date()) {
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

function log(event, node, delay, reason) {
  const line = `${ts()} ${event} ${node || '-'} ${delay == null ? '-' : delay + 'ms'} ${reason}`
    .replace(/[\r\n]+/g, ' ')
    .trimEnd();
  if (VERBOSE || DRY) console.log(line);
  if (DRY) return;
  try {
    fs.appendFileSync(CFG.logPath, line + '\n', 'utf8');
  } catch (e) {
    console.error('写日志失败：' + e.message);
  }
}

function maintainLogs() {
  try {
    fs.writeFileSync(path.join(__dirname, 'clash-guard-node.trace'),
      `${ts()} pid=${process.pid} node=${process.version} argv=${ARG.join(' ') || '(none)'}\n`, 'utf8');
  } catch { /* 启动痕迹写失败不影响运行 */ }
  try {
    const tracePath = path.join(__dirname, 'clash-guard-vbs.trace');
    if (fs.statSync(tracePath).size > 200 * 1024) {
      const lines = fs.readFileSync(tracePath, 'utf16le').replace(/^\uFEFF/, '').split(/\r?\n/).filter(Boolean);
      fs.writeFileSync(tracePath, '\uFEFF' + lines.slice(-500).join('\r\n') + '\r\n', 'utf16le');
    }
  } catch { /* 首次运行时可能还没有 VBS 日志 */ }
  try {
    const lines = fs.readFileSync(CFG.logPath, 'utf8').split(/\r?\n/).filter(Boolean);
    if (lines.length > 2000) {
      fs.writeFileSync(CFG.logPath, lines.slice(-1500).join('\n') + '\n', 'utf8');
    }
  } catch { /* 首次运行时可能还没有切换日志 */ }
}

function api(method, apiPath, body) {
  return new Promise((resolve, reject) => {
    const payload = body === undefined ? undefined : Buffer.from(JSON.stringify(body), 'utf8');
    const headers = {};
    if (CFG.api.secret) headers.Authorization = 'Bearer ' + CFG.api.secret;
    if (payload) {
      headers['Content-Type'] = 'application/json';
      headers['Content-Length'] = payload.length;
    }
    const req = http.request({
      host: CFG.api.host, port: CFG.api.port, path: apiPath, method, headers,
      signal: AbortSignal.timeout(10000),
    }, res => {
      const chunks = [];
      res.on('data', chunk => chunks.push(chunk));
      res.on('error', reject);
      res.on('end', () => resolve({ code: res.statusCode, body: Buffer.concat(chunks).toString('utf8') }));
    });
    req.on('error', reject);
    req.end(payload);
  });
}

async function getSelection() {
  const r = await api('GET', '/proxies').catch(e => {
    throw new ControllerError('读取 Clash API 失败：' + e.message);
  });
  if (r.code !== 200) throw new ControllerError(`GET /proxies 返回 HTTP ${r.code}`);

  let proxies;
  try {
    proxies = JSON.parse(r.body)?.proxies;
  } catch {
    throw new ControllerError('GET /proxies 返回的 JSON 无效');
  }
  if (!proxies || typeof proxies !== 'object' || Array.isArray(proxies)) {
    throw new ControllerError('GET /proxies 缺少代理列表');
  }

  const group = proxies[CFG.group];
  if (group?.type !== 'Selector' || typeof group.now !== 'string' ||
      !Array.isArray(group.all) || group.all.some(name => typeof name !== 'string')) {
    throw new ControllerError(`「${CFG.group}」不是有效的手动选择组`);
  }
  return { now: group.now, members: new Set(group.all), proxies };
}

function isSingaporeNode(name, proxies) {
  if (!name.startsWith('🇸🇬') || INFO_NODE_PAT.test(name)) return false;
  const proxy = proxies[name];
  return typeof proxy?.type === 'string' && proxy.type.length > 0 &&
    !SKIP_TYPES.has(proxy.type) && !('all' in proxy);
}

function hasDirect({ members, proxies }) {
  return members.has(CFG.directName) && proxies[CFG.directName]?.type === 'Direct';
}

async function probeDelay(name, timeoutMs) {
  const params = new URLSearchParams({ timeout: String(timeoutMs), url: CFG.testUrl });
  if (CFG.testUrl === DEFAULT_TEST_URL) params.set('expected', '204');

  try {
    const r = await api('GET', `/proxies/${encodeURIComponent(name)}/delay?${params}`);
    // mihomo 用 503/504 表示节点测速失败。
    if (r.code === 503 || r.code === 504) return { name, reason: `测速 HTTP ${r.code}` };
    if (r.code !== 200) throw new Error(`测速接口返回 HTTP ${r.code}`);

    const delay = JSON.parse(r.body)?.delay;
    if (!Number.isInteger(delay) || delay <= 0) throw new Error('测速接口返回无效延迟');
    return { name, delay };
  } catch (e) {
    return { name, apiError: `${name}：${e.message}` };
  }
}

function checkProbeErrors(results) {
  const failed = results.find(result => result.apiError);
  if (failed) throw new ControllerError(failed.apiError);
}

async function pool(items, limit, fn) {
  const results = new Array(items.length);
  let cursor = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (cursor < items.length) {
      const i = cursor++;
      results[i] = await fn(items[i]);
    }
  });
  await Promise.all(workers);
  return results;
}

async function scanSingapore(candidates) {
  if (VERBOSE) console.log(`扫描 ${candidates.length} 个新加坡节点`);
  const results = await pool(candidates, CFG.scan.concurrency, name => probeDelay(name, CFG.scan.timeoutMs));
  checkProbeErrors(results);
  return results
    .filter(result => result.delay < CFG.scan.maxDelay)
    .sort((a, b) => a.delay - b.delay);
}

function loadState() {
  try {
    const state = JSON.parse(fs.readFileSync(CFG.statePath, 'utf8'));
    if (state && typeof state === 'object' && !Array.isArray(state)) return state;
  } catch { /* 状态文件可以不存在 */ }
  return {};
}

function saveState(state) {
  if (DRY) return;
  const temporary = `${CFG.statePath}.${process.pid}.tmp`;
  try {
    fs.writeFileSync(temporary, JSON.stringify(state, null, 2) + '\n', 'utf8');
    fs.renameSync(temporary, CFG.statePath);
  } catch (e) {
    console.error('写状态失败：' + e.message);
    try { fs.unlinkSync(temporary); } catch { /* 临时文件可能未创建 */ }
  }
}

function ageOf(timestamp) {
  const now = Date.now();
  return Number.isFinite(timestamp) && timestamp > 0 && timestamp <= now ? now - timestamp : Infinity;
}

function recentBadNodes(state) {
  return new Map(Object.entries(state.badNodes || {}).filter(([, stamp]) => ageOf(stamp) < CFG.badTtlMs));
}

// 暂停文件：{"until": <epoch ms>, "reason": "..."}。到期自动恢复守护，文件留着无害。
function pausedUntil() {
  try {
    const pause = JSON.parse(fs.readFileSync(CFG.pausePath, 'utf8').replace(/^\uFEFF/, ''));
    const until = Number(pause?.until);
    return Number.isFinite(until) && Date.now() < until ? { until, reason: pause?.reason || '' } : null;
  } catch { /* 没有暂停文件，或格式不对：按未暂停处理 */ }
  return null;
}

// Windows 命名管道由系统保证单实例，进程退出或被终止时自动释放。
function acquireLock() {
  const pipeName = `\\\\.\\pipe\\dfy-clash-guard-${CFG.api.port}`;
  const server = net.createServer(socket => socket.destroy());
  return new Promise((resolve, reject) => {
    server.once('error', e => {
      if (e.code === 'EADDRINUSE') resolve(null);
      else reject(e);
    });
    server.listen(pipeName, () => resolve(server));
  });
}

async function getMode() {
  const r = await api('GET', '/configs');
  if (r.code !== 200) return null;
  try { return JSON.parse(r.body)?.mode || null; } catch { return null; }
}

// 全局模式下流量实际走 GLOBAL 组，只改「节点选择」不生效，得同步一份。
async function syncGlobal(name) {
  const mode = await getMode().catch(() => null);
  if (mode !== 'global') return;
  try {
    const r = await api('PUT', '/proxies/GLOBAL', { name });
    if (r.code !== 204 && r.code !== 200) throw new Error(`HTTP ${r.code}`);
  } catch (e) {
    log('WARN', name, null, '全局模式同步 GLOBAL 组失败：' + e.message);
  }
}

async function changeSelection(initial, target, state, event, reason, badNodes) {
  const latest = await getSelection();
  const { name, delay } = target;
  if (latest.now !== initial.now) {
    log('SKIP', latest.now, null, '检查期间所选节点已变化，下次再检查');
    return 0;
  }

  if (name === CFG.directName) {
    if (!hasDirect(latest)) {
      log('ERROR', name, null, `「${CFG.group}」里缺少有效的 DIRECT 成员`);
      return 1;
    }
    const addedSingapore = [...latest.members].some(candidate => isSingaporeNode(candidate, latest.proxies) &&
      (!initial.members.has(candidate) || !isSingaporeNode(candidate, initial.proxies)));
    if (addedSingapore) {
      log('SKIP', initial.now, null, '检查期间出现了尚未测速的新加坡节点，下次再检查');
      return 0;
    }
  } else if (!latest.members.has(name) || !isSingaporeNode(name, latest.proxies)) {
    log('SKIP', name, delay, '候选已变更，下次再检查');
    return 0;
  }

  if (DRY) {
    log('DRY-RUN', name, delay, reason);
    return 0;
  }

  try {
    const put = await api('PUT', `/proxies/${encodeURIComponent(CFG.group)}`, { name });
    if (put.code !== 204 && put.code !== 200) throw new Error(`PUT 返回 HTTP ${put.code}`);
    let actual;
    try {
      actual = await getSelection();
    } catch (e) {
      throw new Error('已发送切换，复核失败：' + e.message);
    }
    const valid = name === CFG.directName ? hasDirect(actual) :
      actual.members.has(name) && isSingaporeNode(name, actual.proxies);
    if (actual.now !== name || !valid) throw new Error(`切换未确认，当前选择为 ${actual.now || '(空)'}`);
    await syncGlobal(name);
  } catch (e) {
    log('ERROR', name, delay, e.message);
    return 1;
  }

  const stamp = Date.now();
  state.lastSwitch = stamp;
  state.lastRun = stamp;
  state.lastSwitchTo = name;
  state.lastStatus = name === CFG.directName ? 'DIRECT' : 'OK';
  state.lastOffLog = name === CFG.directName ? stamp : 0;
  state.badNodes = Object.fromEntries(badNodes);
  saveState(state);
  log(event, name, delay, reason);
  return 0;
}

async function tryRestore(selection, state, singapore, sinceSwitch) {
  if (sinceSwitch < CFG.restoreCooldownMs) {
    if (VERBOSE) console.log('刚切到 DIRECT，等待恢复冷却');
    return 0;
  }
  const good = await scanSingapore(singapore);
  if (good.length > 0) {
    return changeSelection(selection, good[0], state, 'PROXY-ON',
      `新加坡 ${good.length}/${singapore.length} 个节点可用，从 DIRECT 恢复代理`, new Map());
  }

  if (DRY || ageOf(state.lastOffLog) >= CFG.offLogEveryMs) {
    log('SKIP', CFG.directName, null, `${singapore.length} 个新加坡节点中暂无可用节点，继续直连`);
    state.lastStatus = 'DIRECT';
    state.lastOffLog = Date.now();
    saveState(state);
  }
  return 0;
}

async function switchOrDirect(selection, state, singapore, currentAllowed) {
  const { now } = selection;
  const candidates = singapore.filter(name => name !== now);
  const badNodes = recentBadNodes(state);
  const preferred = candidates.filter(name => !badNodes.has(name));
  let good = await scanSingapore(preferred);
  if (good.length === 0 && preferred.length < candidates.length) {
    good = await scanSingapore(candidates.filter(name => badNodes.has(name)));
  }

  if (good.length > 0) {
    const target = good[0];
    if (currentAllowed) badNodes.set(now, Date.now());
    badNodes.delete(target.name);
    return changeSelection(selection, target, state, 'SWITCH',
      `从 ${now || '(空)'} 切到可用的新加坡节点`, badNodes);
  }
  return changeSelection(selection, { name: CFG.directName }, state, 'PROXY-OFF',
    `${singapore.length} 个新加坡节点中暂无可用节点，切到 DIRECT`, new Map());
}

async function run() {
  const state = loadState();
  const pause = pausedUntil();
  if (pause) {
    if (VERBOSE || DRY) console.log(`暂停中，到 ${ts(new Date(pause.until))}`);
    if (VERBOSE || DRY || ageOf(state.lastPauseLog) >= CFG.pauseLogEveryMs) {
      log('PAUSED', '-', null, `按暂停文件跳过，恢复时间 ${ts(new Date(pause.until))}`);
      state.lastPauseLog = Date.now();
      saveState(state);
    }
    return 0;
  }
  state.lastPauseLog = 0;

  const selection = await getSelection();
  const { now, members, proxies } = selection;
  const sinceSwitch = state.lastSwitchTo === now ? ageOf(state.lastSwitch) : Infinity;
  const singapore = [...members].filter(name => isSingaporeNode(name, proxies));

  if (now === CFG.directName && hasDirect(selection)) {
    return tryRestore(selection, state, singapore, sinceSwitch);
  }

  // 其他地区、嵌套策略组或失效的当前选择立即纠正。
  const currentAllowed = singapore.includes(now);
  if (!currentAllowed) {
    log('REVERT', now, null, '当前选择不是组内有效的新加坡代理节点，重新选择');
    return switchOrDirect(selection, state, singapore, currentAllowed);
  }

  let result = await probeDelay(now, CFG.judge.timeoutMs);
  checkProbeErrors([result]);
  if (result.delay <= CFG.judge.maxDelay) {
    if (VERBOSE) console.log(`${now} 正常 ${result.delay}ms`);
    return 0;
  }
  if (sinceSwitch < CFG.cooldownMs) {
    log('SKIP', now, result.delay, '测速失败，仍在切换冷却期');
    return 0;
  }

  log('FAIL', now, result.delay, `第 1/2 次，${result.reason || '延迟过高'}`);
  await new Promise(resolve => setTimeout(resolve, CFG.confirmGapMs));
  result = await probeDelay(now, CFG.judge.timeoutMs);
  checkProbeErrors([result]);
  if (result.delay <= CFG.judge.maxDelay) {
    log('OK', now, result.delay, '复测恢复正常');
    return 0;
  }
  log('FAIL', now, result.delay, `第 2/2 次，${result.reason || '延迟过高'}，开始扫描新加坡节点`);
  return switchOrDirect(selection, state, singapore, currentAllowed);
}

async function main() {
  const lock = await acquireLock();
  if (!lock) {
    if (VERBOSE || DRY) console.log('另一实例正在运行，本次跳过');
    return 3;
  }

  try {
    if (!DRY) maintainLogs();
    return await run();
  } finally {
    lock.close();
  }
}

main().then(code => {
  process.exitCode = code;
}).catch(e => {
  log('ERROR', '-', null, e.message);
  if (!VERBOSE && !DRY) console.error(e.message);
  process.exitCode = e instanceof ControllerError ? 2 : 1;
});
