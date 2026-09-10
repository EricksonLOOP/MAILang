#!/usr/bin/env node
/**
 * MAIL integrate protocol — Node.js proof of concept
 *
 * Proves that the protocol is independent of the Python SDK.
 * Uses only Node.js built-in modules: child_process, readline, path, crypto.
 * No npm dependencies.
 *
 * Usage:
 *   MAIL_EXE=../../release/win-x64/Mail.Cli.exe node sdk/js/poc.js
 *
 * Expected output: JSON object with a token starting with "js-", exit code 0.
 */

'use strict';

const { spawn }       = require('child_process');
const readline        = require('readline');
const path            = require('path');
const { randomUUID }  = require('crypto');

const EXE  = process.env.MAIL_EXE
    || path.resolve(__dirname, '../../release/win-x64/Mail.Cli.exe');

const FILE = path.resolve(__dirname, '../../examples/token-echo.mail');

if (!require('fs').existsSync(EXE)) {
    console.error(`[poc] Executable not found: ${EXE}`);
    console.error('      Set MAIL_EXE env var or publish the project first.');
    process.exit(1);
}

// ── Process ───────────────────────────────────────────────────────────────────

const proc = spawn(EXE, ['integrate'], {
    stdio: ['pipe', 'pipe', 'inherit'],   // stdout: protocol; stderr: logs
});

const rl = readline.createInterface({ input: proc.stdout, crlfDelay: Infinity });

// ── State ─────────────────────────────────────────────────────────────────────

let handshakeDone = false;
let execId        = null;

// Pending resolvers keyed by request_id
const pendingTools = {};

// Deferred promises for handshake and run result
let resolveHandshake, rejectHandshake;
let resolveRun,       rejectRun;

const handshakePromise = new Promise((res, rej) => { resolveHandshake = res; rejectHandshake = rej; });
const runPromise       = new Promise((res, rej) => { resolveRun = res;       rejectRun = rej; });

// ── Helpers ───────────────────────────────────────────────────────────────────

function send(msg) {
    proc.stdin.write(JSON.stringify(msg) + '\n');
}

// ── Message dispatcher ────────────────────────────────────────────────────────

rl.on('line', raw => {
    if (!raw.trim()) return;

    let msg;
    try { msg = JSON.parse(raw); }
    catch { console.error('[poc] Received invalid JSON:', raw); return; }

    switch (msg.type) {

        case 'handshake_ok':
            resolveHandshake();
            break;

        case 'handshake_error':
            rejectHandshake(new Error(`Handshake rejected: ${msg.reason}`));
            break;

        case 'run_started':
            // acknowledged
            break;

        case 'event':
            // v1: events arrive in batch after execution; nothing to do here
            break;

        case 'tool_request': {
            const requestId = msg.request_id;
            const tool      = msg.tool;

            if (tool === 'GenerateToken') {
                const token = `js-${randomUUID()}`;
                send({ type: 'tool_response', request_id: requestId, output: { token } });
            } else {
                send({ type: 'tool_error', request_id: requestId,
                       error: `Unknown tool: ${tool}` });
            }
            break;
        }

        case 'result':
            resolveRun(msg.output);
            break;

        case 'run_error':
            rejectRun(new Error(msg.error));
            break;

        case 'protocol_error':
            console.error(`[poc] protocol_error: ${msg.reason}`);
            if (msg.fatal) { rejectHandshake(new Error(msg.reason)); rejectRun(new Error(msg.reason)); }
            break;

        default:
            console.error(`[poc] Unknown message type: ${msg.type}`);
    }
});

proc.on('error', err => {
    rejectHandshake(err);
    rejectRun(err);
});

proc.on('close', code => {
    if (code !== 0) {
        rejectRun(new Error(`Process exited with code ${code}`));
    }
});

// ── Main ──────────────────────────────────────────────────────────────────────

async function main() {
    // 1. Handshake
    send({ type: 'handshake', protocol_version: 1, client: 'js-poc' });
    await handshakePromise;

    // 2. Load (optional validation step)
    // Skipped here — run handles compilation internally.

    // 3. Run
    execId = randomUUID();
    send({
        type:         'run',
        execution_id: execId,
        path:         FILE,
        input:        { prompt: 'hello from js' },
        provider:     'simulated',
    });

    const result = await runPromise;

    console.log(JSON.stringify(result, null, 2));

    if (!result.token || !result.token.startsWith('js-')) {
        console.error(`[poc] FAIL: expected token to start with "js-", got: ${result.token}`);
        process.exitCode = 1;
    }

    proc.stdin.end();
}

main().catch(err => {
    console.error('[poc] Error:', err.message);
    process.exitCode = 1;
    proc.kill();
});
