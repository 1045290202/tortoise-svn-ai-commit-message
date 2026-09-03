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

// ── diff 预算与降级 ─────────────────────────────────────────────────────
// 主流提交信息生成器（Copilot/JetBrains/TortoiseGit）都传 diff，但都设预算。
// 40 万字符 ≈ 10 万+ token，既可能超上下文，模型注意力也散。三级策略：
//   1) diff ≤ DIFF_BUDGET          → 全量传，质量最好；
//   2) DIFF_BUDGET < diff ≤ 跳过线 → 按「Index: 文件」分块贪心打包到预算内，
//                                     未收录文件靠 status/文件清单兜底（仍可见）；
//   3) diff > DIFF_SKIP_THRESHOLD  → 放弃 diff，只传 status + 文件清单。
const DIFF_BUDGET = 50000;        // diff 预算（字符），≈ 1.5 万 token
const DIFF_SKIP_THRESHOLD = 250000; // 超过此长度直接不传 diff（极端大提交）

/** 把 svn diff 按「Index: 文件」切成块；返回 [前导头, ...各文件块]。 */
function splitDiffSections(diff) {
    return diff.split(/(?=^Index: )/m).filter(s => s.trim());
}

/**
 * 按预算裁剪 diff。返回 { text, omitted }：
 *   text     实际放进 prompt 的 diff 内容（可为 ''，表示整体放弃）；
 *   omitted  因预算被排除的文件数（0 = 全量）。
 */
function clipDiff(diff) {
    if (diff.length > DIFF_SKIP_THRESHOLD) return { text: '', omitted: -1 };
    if (diff.length <= DIFF_BUDGET) return { text: diff, omitted: 0 };

    const sections = splitDiffSections(diff);
    const picked = [];
    let used = 0, omitted = 0;
    for (const sec of sections) {
        if (used + sec.length <= DIFF_BUDGET || picked.length === 0) {
            // 第一个文件块即使超预算也要保一份，否则什么意图都推不出来
            if (picked.length > 0 && used + sec.length > DIFF_BUDGET) { omitted++; continue; }
            picked.push(sec);
            used += sec.length;
        } else {
            omitted++;
        }
    }
    return { text: picked.join(''), omitted };
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
        const { text, omitted } = clipDiff(req.diff);
        lines.push('');
        if (omitted === -1) {
            lines.push('## 变更内容（diff 过大已整体省略）');
            lines.push('（本次变更 diff 超过 ' + Math.round(DIFF_SKIP_THRESHOLD / 1000) + 'K 字符，未随附。');
            lines.push('请仅依据上方的变更状态与文件清单推断整体意图，摘要保持宽泛。）');
        } else if (omitted > 0) {
            lines.push('## 变更内容（svn diff，已按预算收录部分文件）');
            lines.push('（diff 过长，已收录预算内前若干文件的完整变更，另有 ' + omitted + ' 个文件未收录，');
            lines.push('其改动可参考变更状态。请结合清单判断整体意图，不要臆测未收录内容。）');
            lines.push(text);
        } else {
            lines.push('## 变更内容（svn diff）');
            lines.push(text);
        }
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

    const prompt = buildPrompt(req);
    const serverPort = await pickFreePort();

    // stream-json + partial：把思考/回答增量逐行吐给宿主，供弹窗实时展示
    // 长 prompt 不能放命令行参数：Windows CreateProcess 命令行上限约 32K 字符，
    // diff 一大就报 ENAMETOOLONG。官方支持管道输入（stdin 与 -p 短指令合并），
    // 因此完整 prompt 走 stdin，-p 只留一句定位指令。
    const args = [cliPath, '-p',
        '完整任务说明已通过标准输入提供（含变更文件清单、svn diff 等），请读取并按其执行。',
        '--output-format', 'stream-json', '--include-partial-messages', '--verbose',
        '--no-session-persistence'];
    if (req.model) args.push('--model', req.model);

    const child = spawn(process.execPath, args, {
        cwd: req.commonRoot && fs.existsSync(req.commonRoot) ? req.commonRoot : os.homedir(),
        stdio: ['pipe', 'pipe', 'pipe'],
        windowsHide: true,
        env: Object.assign({}, process.env, { SERVER__PORT: String(serverPort) }),
    });

    // 写入完整 prompt 后立即关闭 stdin，通知 CLI 输入结束
    child.stdin.on('error', () => { /* CLI 提前退出导致 EPIPE，由 exit/error 分支统一兜底 */ });
    child.stdin.end(prompt, 'utf8');

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
