#!/usr/bin/env node
/**
 * codebuddy-bridge.js —— TortoiseSVN 插件与 WorkBuddy CLI 之间的桥接进程
 *
 * 协议（stdin → stdout，均为 UTF-8）：
 *
 *   stdin  请求（单个 JSON）：
 *   {
 *     "commonRoot":      "E:\\repo\\wc",          // 工作副本共同根目录
 *     "pathList":        ["E:\\repo\\wc\\a.cs"],  // 变更文件列表
 *     "status":          "M   E:\\repo\\wc\\a.cs",// svn status 输出（含 A/M/D/R/? 状态码，可为空）
 *     "diff":            "Index: ...",            // svn diff 内容（可为空）
 *     "originalMessage": "用户已输入的日志",        // 可为空
 *     "timeoutMs":       180000,                  // 可选，默认 180000
 *     "model":           "hy4-preview",           // 可选，不传用 CLI 默认
 *     "cliPath":         "C:\\...\\cli\\bin\\codebuddy",  // 可选，覆盖 CLI 路径
 *     "promptPath":      "D:\\prompts"            // 可选，覆盖提示词文件/所在目录
 *   }
 *
 *   stdout 响应（行式 JSON 事件，便于宿主流式展示）：
 *   {"type":"delta","kind":"thinking","text":"..."}   AI 思考内容（增量）
 *   {"type":"delta","kind":"text","text":"..."}       正式回答内容（增量）
 *   {"type":"done","message":"..."}                   成功，message 为最终提交信息
 *   {"type":"error","error":"..."}                    失败（含 stderr 摘要）
 *
 *   done/error 之后进程退出；exit code 0=成功 1=失败。
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

const DEFAULT_CLI_PATH = 'C:\\Program Files\\WorkBuddy\\resources\\app.asar.unpacked\\cli\\bin\\codebuddy';
const DEFAULT_TIMEOUT_MS = 180000;
const DIFF_HARD_LIMIT = 400000; // 桥接侧二次保险：diff 超过 40 万字符截断

// 提示词外置：指令模板放在脚本同目录的 commit-message-prompt.md，运行时动态加载。
// 解析优先级：请求 promptPath > 环境变量 WORKBUDDY_PROMPT_PATH > 同目录默认文件。
const PROMPT_FILE_NAME = 'commit-message-prompt.md';
// 兜底模板：外部文件缺失/读取失败时使用，保证插件永远能生成。
const FALLBACK_INSTRUCTION = [
    '你是 SVN 提交信息生成器。根据提供的变更信息，产出一条规范的提交日志。',
    '首行格式：类型(范围): 摘要；类型取 feat/fix/refactor/style/docs/chore，摘要 ≤ 50 字符、中文、动词开头、不带句号。',
    '忠实于 diff，不臆测；过滤 IDE/构建产物等噪音；只输出提交信息本身，不要解释或代码块包裹。',
].join('\n');
let instructionCache = null; // 进程内缓存，进程按次拉起，等价于每次调用读取最新文件

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

/**
 * 解析指令模板文件路径：请求覆盖 > 环境变量 > 脚本同目录默认文件。
 * 环境变量 WORKBUDDY_PROMPT_PATH 指向的目录（若为目录）或文件优先于同目录默认。
 */
function resolvePromptPath(req) {
    const pick = (p) => {
        if (!p) return null;
        try {
            if (fs.statSync(p).isDirectory()) return path.join(p, PROMPT_FILE_NAME);
        } catch (_) { /* 不存在则按文件路径处理 */ }
        return p;
    };
    return pick(req.promptPath) || pick(process.env.WORKBUDDY_PROMPT_PATH)
        || path.join(__dirname, PROMPT_FILE_NAME);
}

/** 动态加载外部提示词（进程内缓存一次）；读不到则回退内置兜底模板。 */
function loadInstruction(req) {
    if (instructionCache != null) return instructionCache;
    const promptPath = resolvePromptPath(req);
    try {
        const text = fs.readFileSync(promptPath, 'utf8').trim();
        if (text) {
            instructionCache = text;
            return instructionCache;
        }
    } catch (_) { /* 文件缺失或不可读，走兜底 */ }
    instructionCache = FALLBACK_INSTRUCTION;
    return instructionCache;
}

function buildPrompt(req) {
    const lines = [];
    lines.push(loadInstruction(req));

    const paths = (req.pathList || []).slice(0, 50);
    if (paths.length) {
        lines.push('');
        lines.push('## 变更文件清单（勾选待提交项）');
        for (const p of paths) lines.push('- ' + p);
    }
    if (req.status && req.status.trim()) {
        lines.push('');
        lines.push('## 变更状态（svn status，含 A/M/D/R/? 等状态码）');
        lines.push(req.status);
    }
    if (req.diff && req.diff.trim()) {
        lines.push('');
        lines.push('## 变更内容（svn diff）');
        lines.push(req.diff);
    }
    if (req.originalMessage && req.originalMessage.trim()) {
        lines.push('');
        lines.push('## 用户说明（可作为意图参考，不要原样照抄）');
        lines.push(req.originalMessage);
    }
    return lines.join('\n');
}

function emit(obj) {
    try { process.stdout.write(JSON.stringify(obj) + '\n'); } catch (_) { /* 管道已断则忽略 */ }
}

// ── 主流程 ──────────────────────────────────────────────────────────────

async function main() {
    let req = {};
    const raw = await readStdin();
    try {
        req = JSON.parse(raw || '{}');
    } catch (e) {
        emit({ type: 'error', error: 'stdin 不是合法 JSON: ' + e.message });
        process.exit(1);
    }

    const cliPath = req.cliPath || process.env.WORKBUDDY_CLI_PATH || DEFAULT_CLI_PATH;
    const timeoutMs = Number(req.timeoutMs) > 0 ? Number(req.timeoutMs) : DEFAULT_TIMEOUT_MS;

    if (!fs.existsSync(cliPath)) {
        emit({ type: 'error', error: 'CLI 不存在: ' + cliPath });
        process.exit(1);
    }

    if (req.diff && req.diff.length > DIFF_HARD_LIMIT) {
        req.diff = req.diff.slice(0, DIFF_HARD_LIMIT) + '\n...（diff 过长已截断）';
    }

    const prompt = buildPrompt(req);
    const serverPort = await pickFreePort();

    // stream-json + partial：把思考/回答增量逐行吐给宿主，供弹窗实时展示
    const args = [cliPath, '-p', prompt,
        '--output-format', 'stream-json', '--include-partial-messages', '--verbose',
        '--no-session-persistence'];
    if (req.model) args.push('--model', req.model);

    const child = spawn(process.execPath, args, {
        cwd: req.commonRoot && fs.existsSync(req.commonRoot) ? req.commonRoot : os.homedir(),
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true,
        env: Object.assign({}, process.env, { SERVER__PORT: String(serverPort) }),
    });

    let stderrTail = '';
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', d => { stderrTail = (stderrTail + d).slice(-2000); });

    let sawResult = false;
    const timer = setTimeout(() => {
        emit({ type: 'error', error: 'CLI 调用超时（' + Math.round(timeoutMs / 1000) + 's）' });
        child.kill('SIGKILL');
        setTimeout(() => process.exit(1), 200);
    }, timeoutMs);

    // 逐行解析 CLI 流式输出
    let lineBuf = '';
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => {
        lineBuf += chunk;
        let idx;
        while ((idx = lineBuf.indexOf('\n')) !== -1) {
            const line = lineBuf.slice(0, idx).trim();
            lineBuf = lineBuf.slice(idx + 1);
            if (line) handleLine(line);
        }
    });
    child.stdout.on('end', () => { if (lineBuf.trim()) handleLine(lineBuf.trim()); });

    function handleLine(line) {
        let msg;
        try { msg = JSON.parse(line); } catch (_) { return; } // 跳过非 JSON 行
        if (!msg || typeof msg !== 'object') return;

        // 流式增量：stream_event.event.delta
        if (msg.type === 'stream_event' && msg.event && msg.event.type === 'content_block_delta' && msg.event.delta) {
            const d = msg.event.delta;
            if (d.type === 'thinking_delta' && d.thinking) emit({ type: 'delta', kind: 'thinking', text: d.thinking });
            else if (d.type === 'text_delta' && d.text) emit({ type: 'delta', kind: 'text', text: d.text });
            return;
        }
        // 兜底：某些版本不发 partial，直接给完整 assistant 消息
        if (msg.type === 'assistant' && msg.content) {
            for (const block of msg.content) {
                if (block && block.type === 'text' && block.text) emit({ type: 'delta', kind: 'text', text: block.text });
                if (block && block.type === 'thinking' && block.thinking) emit({ type: 'delta', kind: 'thinking', text: block.thinking });
            }
            return;
        }
        // 最终结果
        if (msg.type === 'result') {
            sawResult = true;
            clearTimeout(timer);
            if (msg.is_error) {
                emit({ type: 'error', error: 'CLI 返回错误' + (stderrTail ? '：' + stderrTail.trim().slice(0, 300) : '') });
                child.kill('SIGKILL');
                setTimeout(() => process.exit(1), 200);
            } else if (msg.result && String(msg.result).trim()) {
                emit({ type: 'done', message: String(msg.result).trim() });
                child.kill('SIGKILL'); // result 已拿到，立即收尾（CLI 事件循环可能不退出）
                setTimeout(() => process.exit(0), 200);
            } else {
                emit({ type: 'error', error: 'CLI 返回空提交信息' });
                child.kill('SIGKILL');
                setTimeout(() => process.exit(1), 200);
            }
        }
    }

    child.on('exit', (code) => {
        if (sawResult) return; // 已处理
        clearTimeout(timer);
        const detail = stderrTail.trim().slice(0, 500);
        emit({ type: 'error', error: detail ? 'CLI 提前退出（code ' + code + '）：' + detail : 'CLI 提前退出（code ' + code + '），未返回结果' });
        setTimeout(() => process.exit(1), 200);
    });
    child.on('error', (e) => {
        clearTimeout(timer);
        emit({ type: 'error', error: '无法启动 CLI: ' + (e && e.message) });
        setTimeout(() => process.exit(1), 200);
    });
}

main().catch(e => {
    emit({ type: 'error', error: '桥接进程异常: ' + (e && e.message) });
    process.exit(1);
});
