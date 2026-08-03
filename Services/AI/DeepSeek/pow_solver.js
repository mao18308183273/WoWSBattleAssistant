// DeepSeek PoW (DeepSeekHashV1) 求解器 - Node.js 版本
// 用 DeepSeek 官方 sha3_wasm_bg.wasm 调用 wasm_solve
//
// 输入(stdin JSON): {challenge, salt, difficulty, expireAt}
// 输出(stdout JSON): {answer, ms} 或 {error}
//
// 调用契约(从 37627.ebf6d8f55d.js worker 反编译):
//   外层 (e,t,r,s,o) = (algorithm, challenge, salt, difficulty, expireAt)
//   prefix = `${salt}_${expireAt}_`     <-- 用 expireAt 不是 difficulty!
//   内层 wasm_solve(stack_ptr, challenge_ptr, challenge_len, prefix_ptr, prefix_len, difficulty)
//   返回值在 stack_ptr 处: int32 status (0=无解, !=0=成功) + float64 answer
//
// 用法: node pow_solver.js <wasm_path>
//   wasm_path 缺省时与本脚本同目录的 sha3_wasm_bg.wasm

const fs = require('fs');
const path = require('path');

const wasmPath = process.argv[2] || path.join(__dirname, 'sha3_wasm_bg.wasm');
const wasmBytes = fs.readFileSync(wasmPath);

let wasm = null;

function mem() {
  return wasm.memory.buffer;
}

// wbindgen passStringToWasm:UTF-8 编码后用 malloc 分配,写入 linear memory
function passStringToWasm(str) {
  const bytes = new TextEncoder().encode(str);
  // __wbindgen_export_0 = malloc(size, align)
  const ptr = wasm.__wbindgen_export_0(bytes.length, 1);
  new Uint8Array(mem(), ptr, bytes.length).set(bytes);
  return { ptr, len: bytes.length };
}

async function init() {
  const { instance } = await WebAssembly.instantiate(wasmBytes, { wbg: {} });
  wasm = instance.exports;
}

function solve(challenge, salt, difficulty, expireAt) {
  const prefix = `${salt}_${expireAt}_`;
  // 在栈上分配 16 字节存放返回值(int32 status + float64 answer)
  const stackPtr = wasm.__wbindgen_add_to_stack_pointer(-16);
  try {
    const ch = passStringToWasm(challenge);
    const pf = passStringToWasm(prefix);
    wasm.wasm_solve(stackPtr, ch.ptr, ch.len, pf.ptr, pf.len, difficulty);
    const view = new DataView(mem(), stackPtr, 16);
    const status = view.getInt32(0, true);   // little endian
    const answer = view.getFloat64(8, true); // little endian
    if (status === 0) return null;
    return answer;
  } finally {
    wasm.__wbindgen_add_to_stack_pointer(16);
  }
}

async function main() {
  await init();
  const input = await new Promise(resolve => {
    let data = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', c => data += c);
    process.stdin.on('end', () => resolve(data));
  });
  const { challenge, salt, difficulty, expireAt } = JSON.parse(input);
  const t0 = Date.now();
  const answer = solve(challenge, salt, difficulty, expireAt);
  const ms = Date.now() - t0;
  if (answer === null) {
    console.log(JSON.stringify({ error: 'No solution found', ms }));
    process.exit(1);
  }
  console.log(JSON.stringify({ answer, ms }));
}

main().catch(e => {
  console.log(JSON.stringify({ error: e.message, stack: e.stack }));
  process.exit(1);
});
