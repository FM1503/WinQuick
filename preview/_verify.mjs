// 原型通用自检器（临时脚本，不进产品）：用 CDP 打开一个本地原型页，
// 读它**页内自检**的结论，并确认没有脚本错误。
//
// 它管的是「跑得起来吗、算错了吗」——**不管观感**（AGENTS.md §1：观感归用户）。
// 断言本身写在各自页面的 selfCheck() 里（那里能拿到页面的函数、几何与浏览器真实值），
// 这个脚本只负责把它读出来并打印，所以对任何原型都能用：
//
//   node preview/_verify.mjs                       # 默认验 form-preview
//   node preview/_verify.mjs file:///.../skin-preview.html
//
import { spawn } from "node:child_process";

const CHROME = process.env.CHROME || "C:/Program Files/Google/Chrome/Application/chrome.exe";
const URL = process.argv[2] || "file:///D:/Work/WinQuick/preview/form-preview.html";
const PORT = Number(process.env.CDP_PORT || 9333);

const chrome = spawn(CHROME, [
  "--headless", "--disable-gpu", "--no-sandbox",
  `--remote-debugging-port=${PORT}`, "--window-size=1360,1200", "about:blank",
], { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function findTarget() {
  for (let i = 0; i < 60; i++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json();
      const page = list.find((t) => t.type === "page");
      if (page) return page;
    } catch { /* 端口还没起来 */ }
    await sleep(250);
  }
  throw new Error("Chrome 调试端口未就绪");
}

const target = await findTarget();
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });

let id = 0;
const pending = new Map();
const events = [];
ws.onmessage = (m) => {
  const msg = JSON.parse(m.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  else if (msg.method) events.push(msg.method);
};
function send(method, params = {}) {
  const mine = ++id;
  ws.send(JSON.stringify({ id: mine, method, params }));
  return new Promise((r) => pending.set(mine, r));
}
async function evaluate(expression) {
  const res = await send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
  if (res.result?.exceptionDetails) throw new Error(res.result.exceptionDetails.text);
  return res.result.result.value;
}

await send("Page.enable");
await send("Runtime.enable");
await send("Page.navigate", { url: URL });
for (let i = 0; i < 80 && !events.includes("Page.loadEventFired"); i++) await sleep(100);
await sleep(700);   // 让页内自检与首次渲染跑完

const r = await evaluate(`(() => ({
  check: document.getElementById("selfcheck") ? document.getElementById("selfcheck").innerText.trim() : "(页面没有 selfcheck 元素)",
  status: document.getElementById("status") ? document.getElementById("status").innerText.trim() : "",
  cards: document.querySelectorAll(".card").length,
  wired: document.querySelectorAll(".card.on").length,
}))()`);

console.log(`原型：${URL}`);
console.log(`选项卡片：${r.cards} 张，当前选中：${r.wired}`);
if (r.status) console.log(`状态行：${r.status}`);
console.log("—— 页内自检 ——");
console.log(r.check);
const failed = /✗/.test(r.check) || /出错|失败/.test(r.check);
console.log(failed ? "\n结论：**有自检项未通过**" : "\n结论：全部通过，且没有脚本错误");

ws.close();
chrome.kill();
process.exit(failed ? 1 : 0);
