#!/usr/bin/env node
/**
 * codebuddy-bridge.js —— TortoiseSVN 插件与 WorkBuddy CLI 之间的桥接进程
 *
 * 协议（stdin → stdout，均为 UTF-8 JSON）：
 *
 *   stdin  请求：
 *   {
 *     "commonRoot":      "E:\\repo\\wc",          // 工作副本共同根目录
 *     "pathList":        ["E:\\repo\\wc\\a.cs"],  // 变更文件列表
 *     "diff":            "Index: ...",            // svn diff 内容（可为空）
 *     "originalMessage": "用户已输入的日志",        // 可为空
 *     "timeoutMs":       180000,                  // 可选，默认 180000
 *     "model":           "hy4-preview",           // 可选，不传用 CLI 默认
 *     "cliPath":         "C:\\...\\cli\\bin\\codebuddy"  // 可选，覆盖 CLI 路径
 *   }
 *
 *   stdout 响应（最后一行 JSON）：
 *   { "ok": true,  "message": "生成的提交信息" }
 *   { "ok": false, "error": "失败原因（含 CLI stderr 摘要）" }
 *
 * 退出码：0 成功 / 1 失败。
 *
 * 已知坑：CLI 内部服务默认监听 127.0.0.1:10003，与 WorkBuddy 桌面端冲突会导致
 * 进程静默挂死（EADDRINUSE 未处理）。因此每次调用前随机挑一个空闲端口，
 * 通过 SERVER__PORT 环境变量传给 CLI 规避。
 */
'use strict';

const { spawn } = require('child_process');
const net = require('net');
const fs = require('fs');
const path = require('path');
const os = require('os');

const DEFAULT_CLI_PATH = 'codebuddy';
const DEFAULT_TIMEOUT_MS = 180000;
const DIFF_HARD_LIMIT = 400000; // 桥接侧二次保险：diff 超过 40 万字符截断

// ── 工具 ────────────────────────────────────────────────────────────────

function readStdin() {
    return new Promise((resolve, reject) => {
        let buf = '';
        process.stdin.setEncoding('utf8');
        process.stdin.on('data', d => { buf += d; });
        process.stdin.on('end', () => resolve(buf));
        process.stdin.on('error', reject);
    });
}

/** 向 OS 要一个空闲 TCP 端口，拿到后立即释放，供 SERVER__PORT 使用。 */
function pickFreePort() {
    return new Promise((resolve, reject) => {
        const srv = net.createServer();
        srv.unref();
        srv.on('error', reject);
        srv.listen(0, '127.0.0.1', () => {
            const port = srv.address().port;
            srv.close(() => resolve(port));
        });
    });
}

function buildPrompt(req) {
    const lines = [];
    lines.push('你是 SVN 提交信息生成器。请根据以下变更内容生成一条提交信息（commit message）。');
    lines.push('');
    lines.push('要求：');
    lines.push('- 第一行为简明总结（不超过 50 字，中文，不加任何前缀）。');
    lines.push('- 若变更较复杂，总结行之后空一行，用简短的条目说明要点；简单变更则只保留总结行。');
    lines.push('- 只输出提交信息本身，不要解释、不要 markdown 代码块、不要引号。');

    const paths = (req.pathList || []).slice(0, 50);
    if (paths.length) {
        lines.push('');
        lines.push('变更文件：');
        for (const p of paths) lines.push('- ' + p);
    }
    if (req.diff && req.diff.trim()) {
        lines.push('');
        lines.push('变更内容（svn diff）：');
        lines.push(req.diff);
    }
    if (req.originalMessage && req.originalMessage.trim()) {
        lines.push('');
        lines.push('用户已输入的日志内容（可作为意图参考，不要原样照抄）：');
        lines.push(req.originalMessage);
    }
    return lines.join('\n');
}

/**
 * 从 CLI stdout 里提取最终结果。
 * CLI --output-format json 输出的是一个消息数组，最后一项形如：
 *   { "type": "result", "is_error": false, "result": "..." }
 */
function extractResult(stdout) {
    // stdout 可能混有非 JSON 前缀，从第一个 '[' 开始逐个尝试解析完整数组
    let idx = stdout.indexOf('[');
    while (idx !== -1) {
        try {
            const arr = JSON.parse(stdout.slice(idx));
            for (let i = arr.length - 1; i >= 0; i--) {
                const item = arr[i];
                if (item && item.type === 'result') {
                    return item; // 命中即返回
                }
            }
        } catch (_) {
            // 从下一个 '[' 重试
        }
        idx = stdout.indexOf('[', idx + 1);
    }
    return null;
}

// ── 主流程 ──────────────────────────────────────────────────────────────

async function main() {
    let req = {};
    const raw = await readStdin();
    try {
        req = JSON.parse(raw || '{}');
    } catch (e) {
        process.stdout.write(JSON.stringify({ ok: false, error: 'stdin 不是合法 JSON: ' + e.message }) + '\n');
        process.exit(1);
    }

    const cliPath = req.cliPath || process.env.WORKBUDDY_CLI_PATH || DEFAULT_CLI_PATH;
    const timeoutMs = Number(req.timeoutMs) > 0 ? Number(req.timeoutMs) : DEFAULT_TIMEOUT_MS;

    if (!fs.existsSync(cliPath)) {
        process.stdout.write(JSON.stringify({ ok: false, error: 'CLI 不存在: ' + cliPath }) + '\n');
        process.exit(1);
    }

    if (req.diff && req.diff.length > DIFF_HARD_LIMIT) {
        req.diff = req.diff.slice(0, DIFF_HARD_LIMIT) + '\n...（diff 过长已截断）';
    }

    const prompt = buildPrompt(req);
    const serverPort = await pickFreePort();

    const args = [cliPath, '-p', prompt, '--output-format', 'json', '--no-session-persistence'];
    if (req.model) args.push('--model', req.model);

    const child = spawn(process.execPath, args, {
        cwd: req.commonRoot && fs.existsSync(req.commonRoot) ? req.commonRoot : os.homedir(),
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true,
        env: Object.assign({}, process.env, { SERVER__PORT: String(serverPort) }),
    });

    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', d => { stdout += d; });
    child.stderr.on('data', d => { stderr += d; });

    let timedOut = false;
    const timer = setTimeout(() => {
        timedOut = true;
        child.kill('SIGKILL');
    }, timeoutMs);

    const exitCode = await new Promise(resolve => {
        child.on('exit', (code) => resolve(code));
        child.on('error', () => resolve(-1));
    });
    clearTimeout(timer);

    const fail = (msg) => {
        const detail = stderr.trim().slice(0, 500);
        process.stdout.write(JSON.stringify({ ok: false, error: detail ? msg + '：' + detail : msg }) + '\n');
        process.exit(1);
    };

    if (timedOut) fail('CLI 调用超时（' + Math.round(timeoutMs / 1000) + 's）');
    if (exitCode !== 0) fail('CLI 退出码 ' + exitCode);

    const result = extractResult(stdout);
    if (!result) fail('无法从 CLI 输出解析结果');
    if (result.is_error) fail('CLI 返回错误');
    if (!result.result || !String(result.result).trim()) fail('CLI 返回空提交信息');

    process.stdout.write(JSON.stringify({ ok: true, message: String(result.result).trim() }) + '\n');
    process.exit(0);
}

main().catch(e => {
    try {
        process.stdout.write(JSON.stringify({ ok: false, error: '桥接进程异常: ' + (e && e.message) }) + '\n');
    } catch (_) { /* ignore */ }
    process.exit(1);
});
