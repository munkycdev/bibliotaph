// The spike's entry points, called by tools/run.mjs through Playwright. Each returns plain data to record.
import { create } from './dotnet-web-worker-client.js';

let worker;

export async function startWorker() {
  const started = performance.now();
  worker = await create(120000, {});
  return { ms: Math.round(performance.now() - started) };
}

export async function call(method, ...args) {
  const result = await worker.invoke(`Spike.Web.${method}`, args, 600000);
  return typeof result === 'string' && /^[[{]/.test(result) ? JSON.parse(result) : result;
}

export function stopWorker() {
  worker?.terminate();
  worker = null;
}
